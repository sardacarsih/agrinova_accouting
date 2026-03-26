using System.Globalization;
using Npgsql;
using NpgsqlTypes;

namespace Accounting.Services;

public sealed partial class PostgresAccessControlService
{
    private sealed class InventoryDraftAutoJournalResult
    {
        public bool IsSuccess { get; init; }

        public string Message { get; init; } = string.Empty;

        public long? JournalId { get; init; }

        public string JournalNo { get; init; } = string.Empty;

        public static InventoryDraftAutoJournalResult SuccessNoJournal(string message)
        {
            return new InventoryDraftAutoJournalResult
            {
                IsSuccess = true,
                Message = message ?? string.Empty,
                JournalId = null,
                JournalNo = string.Empty
            };
        }

        public static InventoryDraftAutoJournalResult Failure(string message)
        {
            return new InventoryDraftAutoJournalResult
            {
                IsSuccess = false,
                Message = message ?? "Gagal membuat jurnal draft inventory.",
                JournalId = null,
                JournalNo = string.Empty
            };
        }
    }

    private sealed class InventoryPendingOutboundDocument
    {
        public long LocationId { get; init; }

        public string SourceType { get; init; } = string.Empty;

        public long SourceId { get; init; }

        public DateTime EventDate { get; init; }

        public string DocumentNo { get; init; } = string.Empty;
    }

    private sealed class InventoryPendingAdjustmentDocument
    {
        public long LocationId { get; init; }

        public long RecalcRunId { get; init; }

        public DateTime EventDate { get; init; }

        public string SourceRefNo { get; init; } = string.Empty;

        public string ValuationMethod { get; init; } = string.Empty;

        public string CogsAccountCode { get; init; } = string.Empty;
    }

    private sealed class InventoryPendingOutboundEventLine
    {
        public long SourceLineId { get; init; }

        public string InventoryAccountCode { get; init; } = string.Empty;

        public string CogsAccountCode { get; init; } = string.Empty;

        public decimal TotalCost { get; init; }
    }

    public async Task<AccessOperationResult> PullInventoryJournalsForPeriodAsync(
        long companyId,
        long? locationId,
        DateTime periodMonth,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureJournalSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        if (companyId <= 0)
        {
            return new AccessOperationResult(false, "Company tidak valid.");
        }

        if (locationId.HasValue && locationId.Value <= 0)
        {
            return new AccessOperationResult(false, "Lokasi tidak valid.");
        }

        var periodStart = GetPeriodMonthStart(periodMonth.Date);
        var periodEnd = periodStart.AddMonths(1).AddDays(-1);

        try
        {
            await using var connection = new NpgsqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var actor = NormalizeActor(actorUsername);
            var permissionFailure = await EnsurePermissionAsync(
                connection,
                transaction,
                actor,
                InventoryModuleCode,
                InventorySubmoduleApiInv,
                PermissionActionPullJournal,
                companyId,
                locationId,
                cancellationToken,
                "Anda tidak memiliki izin untuk menarik jurnal inventory.");
            if (permissionFailure is not null)
            {
                return permissionFailure;
            }

            var companyExists = await IsActiveCompanyExistsAsync(connection, transaction, companyId, cancellationToken);
            if (!companyExists)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Company tidak ditemukan atau tidak aktif.");
            }

            if (locationId.HasValue)
            {
                var locationValid = await IsActiveLocationBelongsToCompanyAsync(
                    connection,
                    transaction,
                    companyId,
                    locationId.Value,
                    cancellationToken);
                if (!locationValid)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(false, "Lokasi tidak ditemukan, tidak aktif, atau bukan milik company terpilih.");
                }
            }

            var accountMap = await LoadPostingAccountMapAsync(connection, transaction, companyId, cancellationToken);
            if (accountMap.Count == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "COA posting account tidak ditemukan untuk company ini.");
            }
            var expenseAccountCodeSet = await LoadExpensePostingAccountCodeSetAsync(connection, transaction, companyId, cancellationToken);

            var createdJournalNos = new List<string>();
            var processedDocumentCount = 0;

            var outboundDocs = await LoadPendingOutboundDocumentsAsync(
                connection,
                transaction,
                companyId,
                locationId,
                periodStart,
                cancellationToken);
            foreach (var doc in outboundDocs)
            {
                var eventLines = await LoadPendingOutboundEventLinesAsync(
                    connection,
                    transaction,
                    companyId,
                    doc.SourceType,
                    doc.SourceId,
                    cancellationToken);
                var lines = new List<InventoryAutoJournalLine>();
                var isInboundDocument =
                    string.Equals(doc.SourceType, InventoryCostSourceStockIn, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(doc.SourceType, InventoryCostSourceOpnamePlus, StringComparison.OrdinalIgnoreCase);
                var lineDescription = doc.SourceType switch
                {
                    InventoryCostSourceStockIn => $"PENERIMAAN BARANG {doc.DocumentNo}",
                    InventoryCostSourceOpnamePlus => $"STOK OPNAME PLUS {doc.DocumentNo}",
                    InventoryCostSourceStockOut => $"PENGELUARAN BARANG {doc.DocumentNo}",
                    InventoryCostSourceOpnameMinus => $"STOK OPNAME MINUS {doc.DocumentNo}",
                    _ => $"COGS {doc.SourceType} {doc.DocumentNo}"
                };
                decimal totalCredit = 0;
                decimal totalDebit = 0;
                foreach (var eventLine in eventLines)
                {
                    var inventoryAccountCode = (eventLine.InventoryAccountCode ?? string.Empty).Trim().ToUpperInvariant();
                    var cogsAccountCode = (eventLine.CogsAccountCode ?? string.Empty).Trim().ToUpperInvariant();
                    var amount = RoundAmount(eventLine.TotalCost);
                    if (amount <= 0)
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(inventoryAccountCode))
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        return new AccessOperationResult(false, $"Akun inventory kosong pada dokumen {doc.DocumentNo}.");
                    }

                    if (!accountMap.ContainsKey(inventoryAccountCode))
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        return new AccessOperationResult(false, $"Akun inventory '{inventoryAccountCode}' tidak ditemukan atau tidak aktif.");
                    }

                    if (string.IsNullOrWhiteSpace(cogsAccountCode))
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        var message = isInboundDocument
                            ? $"Akun kredit dari setting kosong pada dokumen {doc.DocumentNo}."
                            : $"Akun beban GL kosong pada dokumen {doc.DocumentNo}.";
                        return new AccessOperationResult(false, message);
                    }

                    if (!accountMap.ContainsKey(cogsAccountCode))
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        var message = isInboundDocument
                            ? $"Akun kredit setting '{cogsAccountCode}' tidak ditemukan atau tidak aktif."
                            : $"Akun beban GL '{cogsAccountCode}' tidak ditemukan atau tidak aktif.";
                        return new AccessOperationResult(false, message);
                    }

                    if (!isInboundDocument && !expenseAccountCodeSet.Contains(cogsAccountCode))
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        return new AccessOperationResult(false, $"Akun beban GL '{cogsAccountCode}' bukan akun EXPENSE posting.");
                    }

                    lines.Add(new InventoryAutoJournalLine
                    {
                        AccountCode = inventoryAccountCode,
                        Description = lineDescription,
                        Debit = isInboundDocument ? amount : 0,
                        Credit = isInboundDocument ? 0 : amount
                    });
                    lines.Add(new InventoryAutoJournalLine
                    {
                        AccountCode = cogsAccountCode,
                        Description = lineDescription,
                        Debit = isInboundDocument ? 0 : amount,
                        Credit = isInboundDocument ? amount : 0
                    });
                    if (isInboundDocument)
                    {
                        totalDebit += amount;
                        totalCredit += amount;
                    }
                    else
                    {
                        totalDebit += amount;
                        totalCredit += amount;
                    }
                }

                totalCredit = RoundAmount(totalCredit);
                totalDebit = RoundAmount(totalDebit);
                if (totalCredit <= 0 && totalDebit <= 0)
                {
                    continue;
                }

                if (totalCredit <= 0 || totalDebit <= 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(false, $"Dokumen {doc.DocumentNo} tidak memiliki pasangan debit/kredit inventory yang valid.");
                }

                if (Math.Abs(totalDebit - totalCredit) > 0.009m)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(
                        false,
                        $"Dokumen {doc.DocumentNo} tidak seimbang. Debit={totalDebit:N2}, Kredit={totalCredit:N2}.");
                }

                var draft = await CreateDraftInventoryAutoJournalAsync(
                    connection,
                    transaction,
                    companyId,
                    doc.LocationId,
                    doc.EventDate.Date,
                    doc.DocumentNo,
                    lineDescription,
                    lines,
                    accountMap,
                    actor,
                    "SAVE_DRAFT_INVENTORY_COGS_PULL",
                    $"company_id={companyId};location_id={doc.LocationId};source_type={doc.SourceType};source_id={doc.SourceId};period={periodStart:yyyy-MM}",
                    cancellationToken);
                if (!draft.IsSuccess)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(false, draft.Message);
                }

                if (!draft.JournalId.HasValue)
                {
                    continue;
                }

                await MarkOutboundEventsPulledAsync(
                    connection,
                    transaction,
                    companyId,
                    doc.SourceType,
                    doc.SourceId,
                    draft.JournalId.Value,
                    cancellationToken);

                createdJournalNos.Add(draft.JournalNo);
                processedDocumentCount++;
            }

            var adjustmentDocs = await LoadPendingAdjustmentDocumentsAsync(
                connection,
                transaction,
                companyId,
                locationId,
                periodStart,
                cancellationToken);
            foreach (var doc in adjustmentDocs)
            {
                if (string.IsNullOrWhiteSpace(doc.CogsAccountCode))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(false, $"Akun COGS adjustment belum diatur untuk dokumen {doc.SourceRefNo}.");
                }

                if (!accountMap.ContainsKey(doc.CogsAccountCode))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(false, $"Akun COGS '{doc.CogsAccountCode}' tidak ditemukan atau tidak aktif.");
                }

                var deltaByAccount = await LoadPendingAdjustmentDeltaByAccountAsync(
                    connection,
                    transaction,
                    companyId,
                    doc.LocationId,
                    doc.RecalcRunId,
                    cancellationToken);
                var lines = new List<InventoryAutoJournalLine>();
                decimal locationDelta = 0;
                foreach (var deltaEntry in deltaByAccount.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var inventoryAccountCode = (deltaEntry.Key ?? string.Empty).Trim().ToUpperInvariant();
                    var delta = RoundAmount(deltaEntry.Value);
                    if (Math.Abs(delta) <= 0.009m)
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(inventoryAccountCode))
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        return new AccessOperationResult(false, $"Akun inventory kosong pada adjustment {doc.SourceRefNo}.");
                    }

                    if (!accountMap.ContainsKey(inventoryAccountCode))
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        return new AccessOperationResult(false, $"Akun inventory '{inventoryAccountCode}' tidak ditemukan atau tidak aktif.");
                    }

                    lines.Add(new InventoryAutoJournalLine
                    {
                        AccountCode = inventoryAccountCode,
                        Description = $"Adjust inventory {doc.ValuationMethod}",
                        Debit = delta > 0 ? delta : 0,
                        Credit = delta < 0 ? Math.Abs(delta) : 0
                    });
                    locationDelta += delta;
                }

                locationDelta = RoundAmount(locationDelta);
                if (Math.Abs(locationDelta) <= 0.009m)
                {
                    continue;
                }

                lines.Add(new InventoryAutoJournalLine
                {
                    AccountCode = doc.CogsAccountCode,
                    Description = $"Contra COGS valuation {doc.ValuationMethod}",
                    Debit = locationDelta < 0 ? Math.Abs(locationDelta) : 0,
                    Credit = locationDelta > 0 ? locationDelta : 0
                });

                var draft = await CreateDraftInventoryAutoJournalAsync(
                    connection,
                    transaction,
                    companyId,
                    doc.LocationId,
                    doc.EventDate.Date,
                    doc.SourceRefNo,
                    $"Penyesuaian valuasi inventory metode {doc.ValuationMethod}",
                    lines,
                    accountMap,
                    actor,
                    "SAVE_DRAFT_INVENTORY_VAL_ADJUSTMENT_PULL",
                    $"company_id={companyId};location_id={doc.LocationId};recalc_run_id={doc.RecalcRunId};period={periodStart:yyyy-MM}",
                    cancellationToken);
                if (!draft.IsSuccess)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(false, draft.Message);
                }

                if (!draft.JournalId.HasValue)
                {
                    continue;
                }

                await MarkAdjustmentEventsPulledAsync(
                    connection,
                    transaction,
                    companyId,
                    doc.LocationId,
                    doc.RecalcRunId,
                    draft.JournalId.Value,
                    cancellationToken);

                createdJournalNos.Add(draft.JournalNo);
                processedDocumentCount++;
            }

            await InsertAuditLogAsync(
                connection,
                transaction,
                "INVENTORY_SETTING",
                companyId,
                "PULL_INVENTORY_JOURNAL",
                actor,
                $"company_id={companyId};scope={(locationId.HasValue ? "LOCATION" : "COMPANY")};location_id={(locationId ?? 0)};period={periodStart:yyyy-MM};document_count={processedDocumentCount};journal_count={createdJournalNos.Count}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            if (createdJournalNos.Count == 0)
            {
                return new AccessOperationResult(true, $"Tidak ada jurnal inventory pending untuk periode {periodStart:yyyy-MM}.", companyId);
            }

            return new AccessOperationResult(
                true,
                $"Pull jurnal inventory periode {periodStart:yyyy-MM} selesai. Draft dibuat: {string.Join(", ", createdJournalNos)}.",
                companyId);
        }
        catch (Exception ex)
        {
            return new AccessOperationResult(false, $"Gagal menarik jurnal inventory periode {periodEnd:yyyy-MM}: {ex.Message}");
        }
    }
    private async Task<InventoryAdjustmentEventSaveResult> SaveInventoryValuationAdjustmentEventsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        long recalcRunId,
        IReadOnlyDictionary<long, Dictionary<string, decimal>> valuationDiffByLocation,
        IReadOnlyDictionary<long, InventoryCostingSettings> effectiveSettingsByLocation,
        CancellationToken cancellationToken)
    {
        var accountMap = await LoadPostingAccountMapAsync(connection, transaction, companyId, cancellationToken);
        await using (var clearExisting = new NpgsqlCommand(
            "DELETE FROM inv_cost_adjustment_events WHERE recalc_run_id = @recalc_run_id;",
            connection,
            transaction))
        {
            clearExisting.Parameters.AddWithValue("recalc_run_id", recalcRunId);
            await clearExisting.ExecuteNonQueryAsync(cancellationToken);
        }

        var referenceNos = new List<string>();
        var eventDate = DateTime.Today.Date;
        foreach (var locationEntry in valuationDiffByLocation.OrderBy(x => x.Key))
        {
            var locationId = locationEntry.Key;
            var diffByAccount = locationEntry.Value;
            if (diffByAccount.Count == 0)
            {
                continue;
            }

            if (!effectiveSettingsByLocation.TryGetValue(locationId, out var effectiveSettings))
            {
                return InventoryAdjustmentEventSaveResult.Failure($"Pengaturan costing lokasi {locationId} tidak ditemukan.");
            }

            var valuationMethod = NormalizeValuationMethod(effectiveSettings.ValuationMethod);
            var cogsAccountCode = NormalizeAccountCode(effectiveSettings.CogsAccountCode);
            if (string.IsNullOrWhiteSpace(cogsAccountCode))
            {
                return InventoryAdjustmentEventSaveResult.Failure(
                    $"Akun COGS belum diatur untuk lokasi {locationId}. Lengkapi setting lalu jalankan ulang recalculation.");
            }

            if (!accountMap.ContainsKey(cogsAccountCode))
            {
                return InventoryAdjustmentEventSaveResult.Failure(
                    $"Akun COGS '{cogsAccountCode}' tidak ditemukan atau tidak aktif di GL account.");
            }

            var sourceRefNo = $"INV-COST-ADJ-{recalcRunId}-{locationId}";
            var hasRows = false;
            foreach (var accountDiff in diffByAccount.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                var inventoryAccountCode = NormalizeAccountCode(accountDiff.Key);
                var deltaAmount = RoundAmount(accountDiff.Value);
                if (Math.Abs(deltaAmount) <= 0.009m)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(inventoryAccountCode))
                {
                    return InventoryAdjustmentEventSaveResult.Failure(
                        "Terdapat item tanpa akun inventory. Lengkapi akun item/kategori sebelum mengganti metode valuasi.");
                }

                if (!accountMap.ContainsKey(inventoryAccountCode))
                {
                    return InventoryAdjustmentEventSaveResult.Failure(
                        $"Akun inventory '{inventoryAccountCode}' tidak ditemukan atau tidak aktif di GL account.");
                }

                await using var insert = new NpgsqlCommand(@"
INSERT INTO inv_cost_adjustment_events (
    company_id,
    location_id,
    recalc_run_id,
    source_ref_no,
    event_date,
    valuation_method,
    inventory_account_code,
    cogs_account_code,
    delta_amount,
    cogs_journal_id,
    created_at
)
VALUES (
    @company_id,
    @location_id,
    @recalc_run_id,
    @source_ref_no,
    @event_date,
    @valuation_method,
    @inventory_account_code,
    @cogs_account_code,
    @delta_amount,
    NULL,
    NOW()
)
ON CONFLICT (recalc_run_id, location_id, inventory_account_code) DO UPDATE
SET source_ref_no = EXCLUDED.source_ref_no,
    event_date = EXCLUDED.event_date,
    valuation_method = EXCLUDED.valuation_method,
    cogs_account_code = EXCLUDED.cogs_account_code,
    delta_amount = EXCLUDED.delta_amount,
    cogs_journal_id = NULL;", connection, transaction);
                insert.Parameters.AddWithValue("company_id", companyId);
                insert.Parameters.AddWithValue("location_id", locationId);
                insert.Parameters.AddWithValue("recalc_run_id", recalcRunId);
                insert.Parameters.AddWithValue("source_ref_no", sourceRefNo);
                insert.Parameters.AddWithValue("event_date", eventDate);
                insert.Parameters.AddWithValue("valuation_method", valuationMethod);
                insert.Parameters.AddWithValue("inventory_account_code", inventoryAccountCode);
                insert.Parameters.AddWithValue("cogs_account_code", cogsAccountCode);
                insert.Parameters.AddWithValue("delta_amount", deltaAmount);
                await insert.ExecuteNonQueryAsync(cancellationToken);
                hasRows = true;
            }

            if (hasRows)
            {
                referenceNos.Add(sourceRefNo);
            }
        }

        return InventoryAdjustmentEventSaveResult.Success(referenceNos);
    }

    private async Task<InventoryDraftAutoJournalResult> CreateDraftInventoryAutoJournalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        long locationId,
        DateTime journalDate,
        string referenceNo,
        string description,
        IReadOnlyCollection<InventoryAutoJournalLine> rawLines,
        IReadOnlyDictionary<string, long> accountMap,
        string actor,
        string auditAction,
        string auditDetails,
        CancellationToken cancellationToken)
    {
        var normalizedLines = NormalizeDraftJournalLines(rawLines);
        if (normalizedLines.Count == 0)
        {
            return InventoryDraftAutoJournalResult.SuccessNoJournal("Tidak ada nilai jurnal inventory yang perlu ditarik.");
        }

        var totalDebit = RoundAmount(normalizedLines.Sum(x => x.Debit));
        var totalCredit = RoundAmount(normalizedLines.Sum(x => x.Credit));
        if (Math.Abs(totalDebit - totalCredit) > 0.009m)
        {
            return InventoryDraftAutoJournalResult.Failure(
                $"Jurnal draft inventory tidak seimbang. Debit={totalDebit:N2}, Kredit={totalCredit:N2}.");
        }

        var prefix = $"INVD-{journalDate:yyyyMMdd}-";
        var nextSequence = await GetNextSequenceByPrefixAsync(
            connection,
            transaction,
            "gl_journal_headers",
            "journal_no",
            companyId,
            prefix,
            cancellationToken);
        var journalNo = $"{prefix}{nextSequence:0000}";

        long journalId;
        await using (var insertHeader = new NpgsqlCommand(@"
INSERT INTO gl_journal_headers (
    company_id,
    location_id,
    journal_no,
    journal_date,
    period_month,
    reference_no,
    description,
    status,
    created_by,
    created_at,
    updated_at
)
VALUES (
    @company_id,
    @location_id,
    @journal_no,
    @journal_date,
    @period_month,
    @reference_no,
    @description,
    'DRAFT',
    @actor,
    NOW(),
    NOW()
)
RETURNING id;", connection, transaction))
        {
            insertHeader.Parameters.AddWithValue("company_id", companyId);
            insertHeader.Parameters.AddWithValue("location_id", locationId);
            insertHeader.Parameters.AddWithValue("journal_no", journalNo);
            insertHeader.Parameters.AddWithValue("journal_date", journalDate.Date);
            insertHeader.Parameters.AddWithValue("period_month", new DateTime(journalDate.Year, journalDate.Month, 1));
            insertHeader.Parameters.AddWithValue("reference_no", (referenceNo ?? string.Empty).Trim());
            insertHeader.Parameters.AddWithValue("description", (description ?? string.Empty).Trim());
            insertHeader.Parameters.AddWithValue("actor", actor);
            journalId = Convert.ToInt64(await insertHeader.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }

        for (var index = 0; index < normalizedLines.Count; index++)
        {
            var line = normalizedLines[index];
            if (!accountMap.TryGetValue(line.AccountCode, out var accountId))
            {
                return InventoryDraftAutoJournalResult.Failure($"Akun '{line.AccountCode}' tidak ditemukan atau tidak aktif.");
            }

            await using var insertDetail = new NpgsqlCommand(@"
INSERT INTO gl_journal_details (
    header_id,
    line_no,
    account_id,
    description,
    debit,
    credit,
    department_code,
    project_code,
    cost_center_code,
    created_at,
    updated_at
)
VALUES (
    @header_id,
    @line_no,
    @account_id,
    @description,
    @debit,
    @credit,
    '',
    '',
    '',
    NOW(),
    NOW()
);", connection, transaction);
            insertDetail.Parameters.AddWithValue("header_id", journalId);
            insertDetail.Parameters.AddWithValue("line_no", index + 1);
            insertDetail.Parameters.AddWithValue("account_id", accountId);
            insertDetail.Parameters.AddWithValue("description", line.Description);
            insertDetail.Parameters.AddWithValue("debit", line.Debit);
            insertDetail.Parameters.AddWithValue("credit", line.Credit);
            await insertDetail.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertAuditLogAsync(
            connection,
            transaction,
            "JOURNAL",
            journalId,
            auditAction,
            actor,
            $"journal_no={journalNo};{auditDetails}",
            cancellationToken);

        return new InventoryDraftAutoJournalResult
        {
            IsSuccess = true,
            Message = "Jurnal draft inventory berhasil dibuat.",
            JournalId = journalId,
            JournalNo = journalNo
        };
    }

    private static List<InventoryAutoJournalLine> NormalizeDraftJournalLines(IReadOnlyCollection<InventoryAutoJournalLine> rawLines)
    {
        var output = new List<InventoryAutoJournalLine>();
        foreach (var raw in rawLines ?? [])
        {
            var accountCode = (raw.AccountCode ?? string.Empty).Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(accountCode))
            {
                continue;
            }

            var debit = RoundAmount(raw.Debit);
            var credit = RoundAmount(raw.Credit);
            if (debit > 0 && credit > 0)
            {
                var net = RoundAmount(debit - credit);
                if (Math.Abs(net) <= 0.009m)
                {
                    continue;
                }

                debit = net > 0 ? net : 0;
                credit = net < 0 ? Math.Abs(net) : 0;
            }

            if (debit <= 0 && credit <= 0)
            {
                continue;
            }

            output.Add(new InventoryAutoJournalLine
            {
                AccountCode = accountCode,
                Description = (raw.Description ?? string.Empty).Trim(),
                Debit = debit,
                Credit = credit
            });
        }

        return output;
    }

    private static async Task<List<InventoryPendingOutboundDocument>> LoadPendingOutboundDocumentsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        long? locationId,
        DateTime periodStart,
        CancellationToken cancellationToken)
    {
        var output = new List<InventoryPendingOutboundDocument>();
        await using var command = new NpgsqlCommand(@"
SELECT e.location_id,
       e.source_type,
       e.source_id,
       MIN(e.event_date) AS event_date,
       COALESCE(
           MAX(CASE
               WHEN e.source_type IN ('STOCK_IN', 'STOCK_OUT') THEN COALESCE(NULLIF(trim(t.reference_no), ''), NULLIF(trim(t.transaction_no), ''))
               WHEN e.source_type IN ('OPNAME_PLUS', 'OPNAME_MINUS') THEN o.opname_no
               ELSE NULL
           END),
           CONCAT(e.source_type, '-', e.source_id::TEXT)
       ) AS document_no
FROM inv_cost_outbound_events e
LEFT JOIN inv_stock_transactions t
    ON e.source_type IN ('STOCK_IN', 'STOCK_OUT')
   AND t.id = e.source_id
   AND t.company_id = e.company_id
LEFT JOIN inv_stock_opname o
    ON e.source_type IN ('OPNAME_PLUS', 'OPNAME_MINUS')
   AND o.id = e.source_id
   AND o.company_id = e.company_id
WHERE e.company_id = @company_id
  AND e.cogs_journal_id IS NULL
  AND e.source_type IN ('STOCK_IN', 'STOCK_OUT', 'OPNAME_PLUS', 'OPNAME_MINUS')
  AND e.event_date >= @period_start
  AND e.event_date < @period_next
  AND (@location_id IS NULL OR e.location_id = @location_id)
GROUP BY e.location_id, e.source_type, e.source_id
ORDER BY MIN(e.event_date), e.location_id, e.source_id;", connection, transaction);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("period_start", periodStart.Date);
        command.Parameters.AddWithValue("period_next", periodStart.AddMonths(1).Date);
        command.Parameters.Add(new NpgsqlParameter("location_id", NpgsqlDbType.Bigint)
        {
            Value = locationId.HasValue ? locationId.Value : DBNull.Value
        });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(new InventoryPendingOutboundDocument
            {
                LocationId = reader.GetInt64(0),
                SourceType = reader.GetString(1).Trim().ToUpperInvariant(),
                SourceId = reader.GetInt64(2),
                EventDate = reader.GetDateTime(3),
                DocumentNo = reader.GetString(4).Trim().ToUpperInvariant()
            });
        }

        return output;
    }

    private static async Task<List<InventoryPendingOutboundEventLine>> LoadPendingOutboundEventLinesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        string sourceType,
        long sourceId,
        CancellationToken cancellationToken)
    {
        var output = new List<InventoryPendingOutboundEventLine>();
        await using var command = new NpgsqlCommand(@"
SELECT source_line_id,
       COALESCE(NULLIF(trim(inventory_account_code), ''), '') AS inventory_account_code,
       COALESCE(NULLIF(trim(cogs_account_code), ''), '') AS cogs_account_code,
       COALESCE(total_cost, 0) AS total_cost
FROM inv_cost_outbound_events
WHERE company_id = @company_id
  AND source_type = @source_type
  AND source_id = @source_id
  AND cogs_journal_id IS NULL
ORDER BY source_line_id, id;", connection, transaction);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("source_type", sourceType.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("source_id", sourceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(new InventoryPendingOutboundEventLine
            {
                SourceLineId = reader.GetInt64(0),
                InventoryAccountCode = reader.GetString(1).Trim().ToUpperInvariant(),
                CogsAccountCode = reader.GetString(2).Trim().ToUpperInvariant(),
                TotalCost = RoundAmount(reader.GetDecimal(3))
            });
        }

        return output;
    }

    private static async Task MarkOutboundEventsPulledAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        string sourceType,
        long sourceId,
        long journalId,
        CancellationToken cancellationToken)
    {
        await using (var updateEvents = new NpgsqlCommand(@"
UPDATE inv_cost_outbound_events
SET cogs_journal_id = @cogs_journal_id
WHERE company_id = @company_id
  AND source_type = @source_type
  AND source_id = @source_id
  AND cogs_journal_id IS NULL;", connection, transaction))
        {
            updateEvents.Parameters.AddWithValue("cogs_journal_id", journalId);
            updateEvents.Parameters.AddWithValue("company_id", companyId);
            updateEvents.Parameters.AddWithValue("source_type", sourceType.Trim().ToUpperInvariant());
            updateEvents.Parameters.AddWithValue("source_id", sourceId);
            await updateEvents.ExecuteNonQueryAsync(cancellationToken);
        }

        if (string.Equals(sourceType, InventoryCostSourceStockOut, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sourceType, InventoryCostSourceStockIn, StringComparison.OrdinalIgnoreCase))
        {
            await using var updateStockOut = new NpgsqlCommand(@"
UPDATE inv_stock_transactions
SET cogs_journal_id = @cogs_journal_id
WHERE company_id = @company_id
  AND id = @id
  AND cogs_journal_id IS NULL;", connection, transaction);
            updateStockOut.Parameters.AddWithValue("cogs_journal_id", journalId);
            updateStockOut.Parameters.AddWithValue("company_id", companyId);
            updateStockOut.Parameters.AddWithValue("id", sourceId);
            await updateStockOut.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        if (!string.Equals(sourceType, InventoryCostSourceOpnameMinus, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(sourceType, InventoryCostSourceOpnamePlus, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await using var updateOpname = new NpgsqlCommand(@"
UPDATE inv_stock_opname
SET cogs_journal_id = @cogs_journal_id
WHERE company_id = @company_id
  AND id = @id
  AND cogs_journal_id IS NULL;", connection, transaction);
        updateOpname.Parameters.AddWithValue("cogs_journal_id", journalId);
        updateOpname.Parameters.AddWithValue("company_id", companyId);
        updateOpname.Parameters.AddWithValue("id", sourceId);
        await updateOpname.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<InventoryPendingAdjustmentDocument>> LoadPendingAdjustmentDocumentsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        long? locationId,
        DateTime periodStart,
        CancellationToken cancellationToken)
    {
        var output = new List<InventoryPendingAdjustmentDocument>();
        await using var command = new NpgsqlCommand(@"
SELECT location_id,
       recalc_run_id,
       MIN(event_date) AS event_date,
       MAX(source_ref_no) AS source_ref_no,
       MAX(valuation_method) AS valuation_method,
       MAX(cogs_account_code) AS cogs_account_code
FROM inv_cost_adjustment_events
WHERE company_id = @company_id
  AND cogs_journal_id IS NULL
  AND event_date >= @period_start
  AND event_date < @period_next
  AND (@location_id IS NULL OR location_id = @location_id)
GROUP BY location_id, recalc_run_id
ORDER BY MIN(event_date), location_id, recalc_run_id;", connection, transaction);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("period_start", periodStart.Date);
        command.Parameters.AddWithValue("period_next", periodStart.AddMonths(1).Date);
        command.Parameters.Add(new NpgsqlParameter("location_id", NpgsqlDbType.Bigint)
        {
            Value = locationId.HasValue ? locationId.Value : DBNull.Value
        });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(new InventoryPendingAdjustmentDocument
            {
                LocationId = reader.GetInt64(0),
                RecalcRunId = reader.GetInt64(1),
                EventDate = reader.GetDateTime(2),
                SourceRefNo = reader.GetString(3).Trim().ToUpperInvariant(),
                ValuationMethod = NormalizeValuationMethod(reader.GetString(4)),
                CogsAccountCode = NormalizeAccountCode(reader.GetString(5))
            });
        }

        return output;
    }

    private static async Task<Dictionary<string, decimal>> LoadPendingAdjustmentDeltaByAccountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        long locationId,
        long recalcRunId,
        CancellationToken cancellationToken)
    {
        var output = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        await using var command = new NpgsqlCommand(@"
SELECT COALESCE(NULLIF(trim(inventory_account_code), ''), '') AS inventory_account_code,
       COALESCE(SUM(delta_amount), 0) AS total_delta
FROM inv_cost_adjustment_events
WHERE company_id = @company_id
  AND location_id = @location_id
  AND recalc_run_id = @recalc_run_id
  AND cogs_journal_id IS NULL
GROUP BY COALESCE(NULLIF(trim(inventory_account_code), ''), '');", connection, transaction);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);
        command.Parameters.AddWithValue("recalc_run_id", recalcRunId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output[reader.GetString(0).Trim().ToUpperInvariant()] = RoundAmount(reader.GetDecimal(1));
        }

        return output;
    }

    private static async Task MarkAdjustmentEventsPulledAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        long locationId,
        long recalcRunId,
        long journalId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(@"
UPDATE inv_cost_adjustment_events
SET cogs_journal_id = @cogs_journal_id
WHERE company_id = @company_id
  AND location_id = @location_id
  AND recalc_run_id = @recalc_run_id
  AND cogs_journal_id IS NULL;", connection, transaction);
        command.Parameters.AddWithValue("cogs_journal_id", journalId);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);
        command.Parameters.AddWithValue("recalc_run_id", recalcRunId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}


