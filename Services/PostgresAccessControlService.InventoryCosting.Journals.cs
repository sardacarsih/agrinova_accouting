using System.Globalization;
using Npgsql;
using NpgsqlTypes;

namespace Accounting.Services;

public sealed partial class PostgresAccessControlService
{
    private async Task<InventoryAdjustmentJournalResult> CreateInventoryValuationAdjustmentJournalsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        IReadOnlyDictionary<long, Dictionary<string, decimal>> valuationDiffByLocation,
        IReadOnlyDictionary<long, InventoryCostingSettings> effectiveSettingsByLocation,
        string actor,
        CancellationToken cancellationToken)
    {
        var accountMap = await LoadPostingAccountMapAsync(connection, transaction, companyId, cancellationToken);

        var journalNos = new List<string>();
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
                return InventoryAdjustmentJournalResult.Failure(
                    $"Pengaturan costing lokasi {locationId} tidak ditemukan.");
            }

            var valuationMethod = NormalizeValuationMethod(effectiveSettings.ValuationMethod);
            var normalizedCogsAccount = NormalizeAccountCode(effectiveSettings.CogsAccountCode);
            if (string.IsNullOrWhiteSpace(normalizedCogsAccount))
            {
                return InventoryAdjustmentJournalResult.Failure(
                    $"Akun COGS belum diatur untuk lokasi {locationId}. Lengkapi setting lalu jalankan ulang recalculation.");
            }

            if (!accountMap.ContainsKey(normalizedCogsAccount))
            {
                return InventoryAdjustmentJournalResult.Failure(
                    $"Akun COGS '{normalizedCogsAccount}' tidak ditemukan atau tidak aktif di GL account.");
            }

            var lines = new List<InventoryAutoJournalLine>();
            decimal locationDelta = 0;
            foreach (var accountDiff in diffByAccount.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                var accountCode = (accountDiff.Key ?? string.Empty).Trim().ToUpperInvariant();
                var delta = RoundAmount(accountDiff.Value);
                if (Math.Abs(delta) <= 0.009m)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(accountCode))
                {
                    return InventoryAdjustmentJournalResult.Failure(
                        "Terdapat item tanpa akun inventory. Lengkapi akun item/kategori sebelum mengganti metode valuasi.");
                }

                if (!accountMap.ContainsKey(accountCode))
                {
                    return InventoryAdjustmentJournalResult.Failure(
                        $"Akun inventory '{accountCode}' tidak ditemukan atau tidak aktif di GL account.");
                }

                if (delta > 0)
                {
                    lines.Add(new InventoryAutoJournalLine
                    {
                        AccountCode = accountCode,
                        Description = $"Adjust inventory {valuationMethod}",
                        Debit = delta,
                        Credit = 0
                    });
                }
                else
                {
                    lines.Add(new InventoryAutoJournalLine
                    {
                        AccountCode = accountCode,
                        Description = $"Adjust inventory {valuationMethod}",
                        Debit = 0,
                        Credit = Math.Abs(delta)
                    });
                }

                locationDelta += delta;
            }

            locationDelta = RoundAmount(locationDelta);
            if (Math.Abs(locationDelta) <= 0.009m)
            {
                continue;
            }

            if (locationDelta > 0)
            {
                lines.Add(new InventoryAutoJournalLine
                {
                    AccountCode = normalizedCogsAccount,
                    Description = $"Contra COGS valuation {valuationMethod}",
                    Debit = 0,
                    Credit = locationDelta
                });
            }
            else
            {
                lines.Add(new InventoryAutoJournalLine
                {
                    AccountCode = normalizedCogsAccount,
                    Description = $"Contra COGS valuation {valuationMethod}",
                    Debit = Math.Abs(locationDelta),
                    Credit = 0
                });
            }

            var adjustmentDateResult = await ResolveRecalculationJournalDateAsync(
                connection,
                transaction,
                companyId,
                locationId,
                DateTime.Today,
                cancellationToken);
            if (!adjustmentDateResult.IsSuccess)
            {
                return InventoryAdjustmentJournalResult.Failure(adjustmentDateResult.Message);
            }

            var adjustmentDate = adjustmentDateResult.JournalDate;
            var referenceNo = $"INV-COST-ADJ-{adjustmentDate:yyyyMMdd}";
            var description = adjustmentDateResult.IsFallback
                ? $"Penyesuaian valuasi inventory metode {valuationMethod} (fallback period {adjustmentDate:yyyy-MM})"
                : $"Penyesuaian valuasi inventory metode {valuationMethod}";

            var posted = await CreatePostedAutoJournalAsync(
                connection,
                transaction,
                companyId,
                locationId,
                adjustmentDate,
                referenceNo,
                description,
                lines,
                accountMap,
                actor,
                "POST_INVENTORY_VAL_ADJUSTMENT",
                $"company_id={companyId};location_id={locationId};valuation_method={valuationMethod};reference={referenceNo}",
                cancellationToken);
            if (!posted.IsSuccess)
            {
                return InventoryAdjustmentJournalResult.Failure(posted.Message);
            }

            if (!string.IsNullOrWhiteSpace(posted.JournalNo))
            {
                journalNos.Add(posted.JournalNo);
            }
        }

        return InventoryAdjustmentJournalResult.Success(journalNos);
    }

    private static async Task<RecalculationJournalDateResult> ResolveRecalculationJournalDateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        long locationId,
        DateTime requestedDate,
        CancellationToken cancellationToken)
    {
        var requested = requestedDate.Date;
        var requestedOpen = await IsAccountingPeriodOpenAsync(
            connection,
            transaction,
            companyId,
            locationId,
            requested,
            forUpdate: true,
            cancellationToken);
        if (requestedOpen)
        {
            return new RecalculationJournalDateResult(true, requested, false, string.Empty);
        }

        var currentDate = DateTime.Today.Date;
        await EnsureAccountingPeriodRowAsync(connection, transaction, companyId, locationId, currentDate, cancellationToken);
        var currentOpen = await IsAccountingPeriodOpenAsync(
            connection,
            transaction,
            companyId,
            locationId,
            currentDate,
            forUpdate: true,
            cancellationToken);
        if (currentOpen)
        {
            return new RecalculationJournalDateResult(true, currentDate, true, string.Empty);
        }

        await using var command = new NpgsqlCommand(@"
SELECT period_month
FROM gl_accounting_periods
WHERE company_id = @company_id
  AND location_id = @location_id
  AND is_open = TRUE
ORDER BY period_month DESC
LIMIT 1
FOR UPDATE;", connection, transaction);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        if (scalar is DateTime periodMonth)
        {
            return new RecalculationJournalDateResult(true, periodMonth.Date, true, string.Empty);
        }

        return new RecalculationJournalDateResult(
            false,
            requested,
            false,
            $"Periode akuntansi lokasi {locationId} tidak memiliki periode OPEN untuk posting jurnal adjustment hasil recalculation.");
    }

    private async Task<InventoryJournalCreateResult> CreateInventoryCogsJournalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        long locationId,
        DateTime journalDate,
        string referenceNo,
        string description,
        string cogsAccountCode,
        IReadOnlyDictionary<string, decimal> inventoryCreditByAccount,
        string actor,
        CancellationToken cancellationToken)
    {
        var normalizedCogsAccount = (cogsAccountCode ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalizedCogsAccount))
        {
            return InventoryJournalCreateResult.Failure("Akun COGS belum diatur di Inventory Costing Settings.");
        }

        var accountMap = await LoadPostingAccountMapAsync(connection, transaction, companyId, cancellationToken);
        if (!accountMap.ContainsKey(normalizedCogsAccount))
        {
            return InventoryJournalCreateResult.Failure(
                $"Akun COGS '{normalizedCogsAccount}' tidak ditemukan atau tidak aktif di GL account.");
        }

        var lines = new List<InventoryAutoJournalLine>();
        decimal totalCredit = 0;
        foreach (var entry in inventoryCreditByAccount)
        {
            var accountCode = (entry.Key ?? string.Empty).Trim().ToUpperInvariant();
            var credit = RoundAmount(entry.Value);
            if (string.IsNullOrWhiteSpace(accountCode) || credit <= 0)
            {
                continue;
            }

            if (!accountMap.ContainsKey(accountCode))
            {
                return InventoryJournalCreateResult.Failure(
                    $"Akun inventory '{accountCode}' tidak ditemukan atau tidak aktif di GL account.");
            }

            lines.Add(new InventoryAutoJournalLine
            {
                AccountCode = accountCode,
                Description = description,
                Debit = 0,
                Credit = credit
            });
            totalCredit += credit;
        }

        totalCredit = RoundAmount(totalCredit);
        if (totalCredit <= 0)
        {
            return InventoryJournalCreateResult.SuccessNoJournal();
        }

        lines.Add(new InventoryAutoJournalLine
        {
            AccountCode = normalizedCogsAccount,
            Description = description,
            Debit = totalCredit,
            Credit = 0
        });

        var posted = await CreatePostedAutoJournalAsync(
            connection,
            transaction,
            companyId,
            locationId,
            journalDate.Date,
            referenceNo,
            description,
            lines,
            accountMap,
            actor,
            "POST_INVENTORY_COGS",
            $"company_id={companyId};location_id={locationId};reference={referenceNo}",
            cancellationToken);
        if (!posted.IsSuccess)
        {
            return InventoryJournalCreateResult.Failure(posted.Message);
        }

        return new InventoryJournalCreateResult
        {
            IsSuccess = true,
            Message = posted.Message,
            JournalId = posted.JournalId,
            JournalNo = posted.JournalNo
        };
    }

    private static async Task<Dictionary<string, long>> LoadPostingAccountMapAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        CancellationToken cancellationToken)
    {
        var output = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        await using var command = new NpgsqlCommand(@"
SELECT id, account_code
FROM gl_accounts
WHERE company_id = @company_id
  AND is_active = TRUE
  AND is_posting = TRUE;", connection, transaction);
        command.Parameters.AddWithValue("company_id", companyId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var code = reader.GetString(1).Trim().ToUpperInvariant();
            output[code] = reader.GetInt64(0);
        }

        return output;
    }

    private static async Task<HashSet<string>> LoadExpensePostingAccountCodeSetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        CancellationToken cancellationToken)
    {
        var output = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = new NpgsqlCommand(@"
SELECT account_code
FROM gl_accounts
WHERE company_id = @company_id
  AND is_active = TRUE
  AND is_posting = TRUE
  AND upper(account_type) = 'EXPENSE';", connection, transaction);
        command.Parameters.AddWithValue("company_id", companyId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var code = reader.GetString(0).Trim().ToUpperInvariant();
            if (!string.IsNullOrWhiteSpace(code))
            {
                output.Add(code);
            }
        }

        return output;
    }

    private async Task<InventoryPostedJournalResult> CreatePostedAutoJournalAsync(
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
        var normalizedLines = NormalizeJournalLines(rawLines);
        if (normalizedLines.Count == 0)
        {
            return InventoryPostedJournalResult.SuccessNoJournal("Tidak ada nilai jurnal yang perlu diposting.");
        }

        var totalDebit = RoundAmount(normalizedLines.Sum(x => x.Debit));
        var totalCredit = RoundAmount(normalizedLines.Sum(x => x.Credit));
        if (Math.Abs(totalDebit - totalCredit) > 0.009m)
        {
            return InventoryPostedJournalResult.Failure(
                $"Jurnal otomatis tidak seimbang. Debit={totalDebit:N2}, Kredit={totalCredit:N2}.");
        }

        var isPeriodOpen = await IsAccountingPeriodOpenAsync(
            connection,
            transaction,
            companyId,
            locationId,
            journalDate.Date,
            forUpdate: true,
            cancellationToken);
        if (!isPeriodOpen)
        {
            return InventoryPostedJournalResult.Failure(
                $"Periode akuntansi {journalDate:yyyy-MM} sudah ditutup. Jurnal otomatis inventory gagal diposting.");
        }

        var prefix = $"INV-{journalDate:yyyyMMdd}-";
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
    reference_no,
    description,
    status,
    posted_at,
    posted_by,
    created_by,
    created_at,
    updated_at
)
VALUES (
    @company_id,
    @location_id,
    @journal_no,
    @journal_date,
    @reference_no,
    @description,
    'POSTED',
    NOW(),
    @actor,
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
            insertHeader.Parameters.AddWithValue("reference_no", (referenceNo ?? string.Empty).Trim());
            insertHeader.Parameters.AddWithValue("description", (description ?? string.Empty).Trim());
            insertHeader.Parameters.AddWithValue("actor", actor);
            journalId = Convert.ToInt64(await insertHeader.ExecuteScalarAsync(cancellationToken));
        }

        for (var index = 0; index < normalizedLines.Count; index++)
        {
            var line = normalizedLines[index];
            if (!accountMap.TryGetValue(line.AccountCode, out var accountId))
            {
                return InventoryPostedJournalResult.Failure(
                    $"Akun '{line.AccountCode}' tidak ditemukan atau tidak aktif.");
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

        var ledgerRows = await InsertLedgerEntriesForJournalAsync(
            connection,
            transaction,
            journalId,
            companyId,
            locationId,
            journalDate,
            actor,
            cancellationToken);
        if (ledgerRows <= 0)
        {
            return InventoryPostedJournalResult.Failure("Gagal membentuk ledger jurnal otomatis inventory.");
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

        return new InventoryPostedJournalResult
        {
            IsSuccess = true,
            Message = "Jurnal otomatis inventory berhasil diposting.",
            JournalId = journalId,
            JournalNo = journalNo
        };
    }

    private static List<InventoryAutoJournalLine> NormalizeJournalLines(IReadOnlyCollection<InventoryAutoJournalLine> rawLines)
    {
        var grouped = new Dictionary<string, (string Description, decimal Debit, decimal Credit)>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in rawLines ?? [])
        {
            var accountCode = (raw.AccountCode ?? string.Empty).Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(accountCode))
            {
                continue;
            }

            var debit = RoundAmount(raw.Debit);
            var credit = RoundAmount(raw.Credit);
            if (debit <= 0 && credit <= 0)
            {
                continue;
            }

            var description = (raw.Description ?? string.Empty).Trim();
            if (!grouped.TryGetValue(accountCode, out var current))
            {
                grouped[accountCode] = (description, debit, credit);
                continue;
            }

            var mergedDescription = string.IsNullOrWhiteSpace(current.Description) ? description : current.Description;
            grouped[accountCode] = (
                mergedDescription,
                current.Debit + debit,
                current.Credit + credit);
        }

        var output = new List<InventoryAutoJournalLine>();
        foreach (var entry in grouped.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            var net = RoundAmount(entry.Value.Debit - entry.Value.Credit);
            if (Math.Abs(net) <= 0.009m)
            {
                continue;
            }

            output.Add(new InventoryAutoJournalLine
            {
                AccountCode = entry.Key,
                Description = entry.Value.Description,
                Debit = net > 0 ? net : 0,
                Credit = net < 0 ? Math.Abs(net) : 0
            });
        }

        var totalDebit = RoundAmount(output.Sum(x => x.Debit));
        var totalCredit = RoundAmount(output.Sum(x => x.Credit));
        var diff = RoundAmount(totalDebit - totalCredit);
        if (Math.Abs(diff) > 0.009m)
        {
            if (diff > 0)
            {
                var target = output.FirstOrDefault(x => x.Credit > 0);
                if (target is not null)
                {
                    target.Credit = RoundAmount(target.Credit + diff);
                }
            }
            else
            {
                var target = output.FirstOrDefault(x => x.Debit > 0);
                if (target is not null)
                {
                    target.Debit = RoundAmount(target.Debit + Math.Abs(diff));
                }
            }
        }

        return output
            .Where(x => x.Debit > 0 || x.Credit > 0)
            .ToList();
    }

    private readonly record struct RecalculationJournalDateResult(
        bool IsSuccess,
        DateTime JournalDate,
        bool IsFallback,
        string Message);
}
