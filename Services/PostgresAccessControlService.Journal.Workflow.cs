using Npgsql;
using NpgsqlTypes;

namespace Accounting.Services;

public sealed partial class PostgresAccessControlService
{
    public async Task<ManagedJournalBundle?> GetJournalBundleAsync(
        long journalId,
        long companyId,
        long locationId,
        string actorUsername = "",
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureJournalSchemaAsync(cancellationToken);

        if (journalId <= 0 || companyId <= 0 || locationId <= 0)
        {
            return null;
        }

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        if (!await HasAnyPermissionAsync(
                connection,
                transaction: null,
                NormalizeActor(actorUsername),
                AccountingModuleCode,
                AccountingSubmoduleTransactions,
                AccountingTransactionsReadActions,
                companyId,
                locationId,
                cancellationToken))
        {
            return null;
        }

        ManagedJournalHeader? header = null;
        await using (var headerCommand = new NpgsqlCommand(@"
SELECT id,
       company_id,
       location_id,
       journal_no,
       journal_date,
       period_month,
       COALESCE(reference_no, ''),
       COALESCE(description, ''),
       status
FROM gl_journal_headers
WHERE id = @id
  AND company_id = @company_id
  AND location_id = @location_id;", connection))
        {
            headerCommand.Parameters.AddWithValue("id", journalId);
            headerCommand.Parameters.AddWithValue("company_id", companyId);
            headerCommand.Parameters.AddWithValue("location_id", locationId);

            await using var reader = await headerCommand.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                header = new ManagedJournalHeader
                {
                    Id = reader.GetInt64(0),
                    CompanyId = reader.GetInt64(1),
                    LocationId = reader.GetInt64(2),
                    JournalNo = reader.GetString(3),
                    JournalDate = reader.GetDateTime(4),
                    PeriodMonth = reader.GetDateTime(5),
                    ReferenceNo = reader.GetString(6),
                    Description = reader.GetString(7),
                    Status = reader.GetString(8)
                };
            }
        }

        if (header is null)
        {
            return null;
        }

        var lines = new List<ManagedJournalLine>();
        await using (var lineCommand = new NpgsqlCommand(@"
SELECT d.line_no,
       a.account_code,
       a.account_name,
       COALESCE(d.description, ''),
       d.debit,
       d.credit,
       COALESCE(d.department_code, ''),
       COALESCE(d.project_code, ''),
       d.cost_center_id,
       COALESCE(d.cost_center_code, '')
FROM gl_journal_details d
JOIN gl_accounts a ON a.id = d.account_id
WHERE d.header_id = @header_id
ORDER BY d.line_no;", connection))
        {
            lineCommand.Parameters.AddWithValue("header_id", header.Id);
            await using var reader = await lineCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                lines.Add(new ManagedJournalLine
                {
                    LineNo = reader.GetInt32(0),
                    AccountCode = reader.GetString(1),
                    AccountName = reader.GetString(2),
                    Description = reader.GetString(3),
                    Debit = reader.GetDecimal(4),
                    Credit = reader.GetDecimal(5),
                    DepartmentCode = reader.GetString(6),
                    ProjectCode = reader.GetString(7),
                    CostCenterId = reader.IsDBNull(8) ? null : reader.GetInt64(8),
                    CostCenterCode = reader.GetString(9)
                });
            }
        }

        return new ManagedJournalBundle
        {
            Header = header,
            Lines = lines
        };
    }

    public async Task<AccessOperationResult> SaveJournalDraftAsync(
        ManagedJournalHeader header,
        IReadOnlyCollection<ManagedJournalLine> lines,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureJournalSchemaAsync(cancellationToken);

        if (header.CompanyId <= 0 || header.LocationId <= 0)
        {
            return new AccessOperationResult(false, "Perusahaan dan lokasi jurnal wajib dipilih.");
        }

        if (string.IsNullOrWhiteSpace(header.JournalNo))
        {
            return new AccessOperationResult(false, "Nomor jurnal wajib diisi.");
        }

        var normalizedLines = (lines ?? Array.Empty<ManagedJournalLine>())
            .Where(x => !string.IsNullOrWhiteSpace(x.AccountCode) && (x.Debit > 0 || x.Credit > 0))
            .Select((x, idx) => new ManagedJournalLine
            {
                LineNo = idx + 1,
                AccountCode = x.AccountCode.Trim().ToUpperInvariant(),
                AccountName = x.AccountName,
                Description = x.Description?.Trim() ?? string.Empty,
                Debit = Math.Round(x.Debit, 2),
                Credit = Math.Round(x.Credit, 2),
                DepartmentCode = x.DepartmentCode?.Trim() ?? string.Empty,
                ProjectCode = x.ProjectCode?.Trim() ?? string.Empty,
                CostCenterId = x.CostCenterId is > 0 ? x.CostCenterId : null,
                CostCenterCode = NormalizeCostCenterCode(x.CostCenterCode)
            })
            .ToList();

        if (normalizedLines.Count == 0)
        {
            return new AccessOperationResult(false, "Detail jurnal minimal harus memiliki satu baris valid.");
        }

        if (normalizedLines.Any(x => x.Debit < 0 || x.Credit < 0 || (x.Debit > 0 && x.Credit > 0) || (x.Debit == 0 && x.Credit == 0)))
        {
            return new AccessOperationResult(false, "Setiap baris detail harus berisi debit atau kredit yang valid.");
        }

        var totalDebit = normalizedLines.Sum(x => x.Debit);
        var totalCredit = normalizedLines.Sum(x => x.Credit);
        if (totalDebit != totalCredit)
        {
            return new AccessOperationResult(false, "Total debit dan kredit harus seimbang.");
        }

        var normalizedNo = header.JournalNo.Trim().ToUpperInvariant();
        var normalizedRef = header.ReferenceNo?.Trim() ?? string.Empty;
        var normalizedDesc = header.Description?.Trim() ?? string.Empty;
        var journalDate = header.JournalDate.Date;
        var periodMonth = GetPeriodMonthStart(header.PeriodMonth == default ? journalDate : header.PeriodMonth);

        try
        {
            await using var connection = new NpgsqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var accountMap = new Dictionary<string, (long Id, string Name, bool RequiresCostCenter)>(StringComparer.OrdinalIgnoreCase);
            await using (var accountCommand = new NpgsqlCommand(@"
SELECT id,
       account_code,
       account_name,
       COALESCE(requires_cost_center, FALSE) AS requires_cost_center
FROM gl_accounts
WHERE company_id = @company_id
  AND is_active = TRUE
  AND is_posting = TRUE;", connection, transaction))
            {
                accountCommand.Parameters.AddWithValue("company_id", header.CompanyId);
                await using var reader = await accountCommand.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    accountMap[reader.GetString(1)] = (
                        reader.GetInt64(0),
                        reader.GetString(2),
                        !reader.IsDBNull(3) && reader.GetBoolean(3));
                }
            }

            var costCentersById = new Dictionary<long, (long Id, string Code, bool IsPosting, bool IsActive)>();
            var costCentersByCode = new Dictionary<string, (long Id, string Code, bool IsPosting, bool IsActive)>(StringComparer.OrdinalIgnoreCase);
            await using (var costCenterCommand = new NpgsqlCommand(@"
SELECT id,
       cost_center_code,
       is_posting,
       is_active
FROM gl_cost_centers
WHERE company_id = @company_id
  AND location_id = @location_id;", connection, transaction))
            {
                costCenterCommand.Parameters.AddWithValue("company_id", header.CompanyId);
                costCenterCommand.Parameters.AddWithValue("location_id", header.LocationId);
                await using var reader = await costCenterCommand.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var record = (
                        Id: reader.GetInt64(0),
                        Code: reader.GetString(1),
                        IsPosting: !reader.IsDBNull(2) && reader.GetBoolean(2),
                        IsActive: !reader.IsDBNull(3) && reader.GetBoolean(3));
                    costCentersById[record.Id] = record;
                    costCentersByCode[record.Code] = record;
                }
            }

            foreach (var line in normalizedLines)
            {
                if (!accountMap.TryGetValue(line.AccountCode, out var accountData))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(false, $"Kode akun tidak ditemukan/aktif: {line.AccountCode}.");
                }

                line.AccountName = accountData.Name;

                (long Id, string Code, bool IsPosting, bool IsActive)? costCenter = null;
                if (line.CostCenterId is > 0)
                {
                    if (!costCentersById.TryGetValue(line.CostCenterId.Value, out var mappedById))
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        return new AccessOperationResult(false, $"Cost center id {line.CostCenterId.Value} tidak ditemukan untuk lokasi jurnal.");
                    }

                    costCenter = mappedById;
                }
                else if (!string.IsNullOrWhiteSpace(line.CostCenterCode))
                {
                    if (!costCentersByCode.TryGetValue(line.CostCenterCode, out var mappedByCode))
                    {
                        if (accountData.RequiresCostCenter)
                        {
                            await transaction.RollbackAsync(cancellationToken);
                            return new AccessOperationResult(false, $"Cost center '{line.CostCenterCode}' tidak ditemukan untuk lokasi jurnal.");
                        }
                    }
                    else
                    {
                        costCenter = mappedByCode;
                    }
                }

                if (costCenter.HasValue)
                {
                    if (!costCenter.Value.IsActive)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        return new AccessOperationResult(false, $"Cost center '{costCenter.Value.Code}' nonaktif.");
                    }

                    if (!costCenter.Value.IsPosting)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        return new AccessOperationResult(false, $"Cost center '{costCenter.Value.Code}' bukan level posting.");
                    }

                    line.CostCenterId = costCenter.Value.Id;
                    line.CostCenterCode = costCenter.Value.Code;
                }
                else
                {
                    line.CostCenterId = null;
                    line.CostCenterCode = NormalizeCostCenterCode(line.CostCenterCode);
                }

                if (accountData.RequiresCostCenter && !line.CostCenterId.HasValue)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(false, $"Akun '{line.AccountCode}' wajib memakai cost center aktif level posting.");
                }
            }

            var actor = NormalizeActor(actorUsername);
            var permissionFailure = await EnsurePermissionAsync(
                connection,
                transaction,
                actor,
                AccountingModuleCode,
                AccountingSubmoduleTransactions,
                ResolveWriteAction(header.Id),
                header.CompanyId,
                header.LocationId,
                cancellationToken,
                "Anda tidak memiliki izin untuk menyimpan draft jurnal.");
            if (permissionFailure is not null)
            {
                return permissionFailure;
            }

            var isPeriodOpen = await IsAccountingPeriodOpenAsync(
                connection,
                transaction,
                header.CompanyId,
                header.LocationId,
                periodMonth,
                forUpdate: true,
                cancellationToken);
            if (!isPeriodOpen)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(
                    false,
                    $"Periode akuntansi {periodMonth:yyyy-MM} sudah ditutup. Draft jurnal tidak dapat disimpan.");
            }

            long journalId;
            if (header.Id <= 0)
            {
                await using var insertHeader = new NpgsqlCommand(@"
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
    updated_at)
VALUES (
    @company_id,
    @location_id,
    @journal_no,
    @journal_date,
    @period_month,
    @reference_no,
    @description,
    'DRAFT',
    @created_by,
    NOW(),
    NOW())
RETURNING id;", connection, transaction);

                insertHeader.Parameters.AddWithValue("company_id", header.CompanyId);
                insertHeader.Parameters.AddWithValue("location_id", header.LocationId);
                insertHeader.Parameters.AddWithValue("journal_no", normalizedNo);
                insertHeader.Parameters.AddWithValue("journal_date", journalDate);
                insertHeader.Parameters.AddWithValue("period_month", periodMonth);
                insertHeader.Parameters.AddWithValue("reference_no", normalizedRef);
                insertHeader.Parameters.AddWithValue("description", normalizedDesc);
                insertHeader.Parameters.AddWithValue("created_by", actor);
                journalId = Convert.ToInt64(await insertHeader.ExecuteScalarAsync(cancellationToken));
            }
            else
            {
                await using var lockHeader = new NpgsqlCommand(@"
SELECT status
FROM gl_journal_headers
WHERE id = @id
  AND company_id = @company_id
  AND location_id = @location_id
FOR UPDATE;", connection, transaction);
                lockHeader.Parameters.AddWithValue("id", header.Id);
                lockHeader.Parameters.AddWithValue("company_id", header.CompanyId);
                lockHeader.Parameters.AddWithValue("location_id", header.LocationId);

                string? currentStatus = null;
                await using (var reader = await lockHeader.ExecuteReaderAsync(cancellationToken))
                {
                    if (await reader.ReadAsync(cancellationToken))
                    {
                        currentStatus = reader.GetString(0);
                    }
                }

                if (string.IsNullOrWhiteSpace(currentStatus))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(false, "Data jurnal tidak ditemukan.");
                }

                if (!string.Equals(currentStatus, "DRAFT", StringComparison.OrdinalIgnoreCase))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(false, "Hanya jurnal draft yang bisa diubah.");
                }

                await using var updateHeader = new NpgsqlCommand(@"
UPDATE gl_journal_headers
SET journal_no = @journal_no,
    journal_date = @journal_date,
    period_month = @period_month,
    reference_no = @reference_no,
    description = @description,
    updated_at = NOW()
WHERE id = @id;", connection, transaction);
                updateHeader.Parameters.AddWithValue("id", header.Id);
                updateHeader.Parameters.AddWithValue("journal_no", normalizedNo);
                updateHeader.Parameters.AddWithValue("journal_date", journalDate);
                updateHeader.Parameters.AddWithValue("period_month", periodMonth);
                updateHeader.Parameters.AddWithValue("reference_no", normalizedRef);
                updateHeader.Parameters.AddWithValue("description", normalizedDesc);
                await updateHeader.ExecuteNonQueryAsync(cancellationToken);

                journalId = header.Id;
            }

            await using (var clearDetails = new NpgsqlCommand(
                "DELETE FROM gl_journal_details WHERE header_id = @header_id;",
                connection,
                transaction))
            {
                clearDetails.Parameters.AddWithValue("header_id", journalId);
                await clearDetails.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var line in normalizedLines)
            {
                var accountId = accountMap[line.AccountCode].Id;
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
    cost_center_id,
    cost_center_code,
    created_at,
    updated_at)
VALUES (
    @header_id,
    @line_no,
    @account_id,
    @description,
    @debit,
    @credit,
    @department_code,
    @project_code,
    @cost_center_id,
    @cost_center_code,
    NOW(),
    NOW());", connection, transaction);

                insertDetail.Parameters.AddWithValue("header_id", journalId);
                insertDetail.Parameters.AddWithValue("line_no", line.LineNo);
                insertDetail.Parameters.AddWithValue("account_id", accountId);
                insertDetail.Parameters.AddWithValue("description", line.Description);
                insertDetail.Parameters.AddWithValue("debit", line.Debit);
                insertDetail.Parameters.AddWithValue("credit", line.Credit);
                insertDetail.Parameters.AddWithValue("department_code", line.DepartmentCode);
                insertDetail.Parameters.AddWithValue("project_code", line.ProjectCode);
                insertDetail.Parameters.AddWithValue("cost_center_id", NpgsqlDbType.Bigint, line.CostCenterId.HasValue ? line.CostCenterId.Value : DBNull.Value);
                insertDetail.Parameters.AddWithValue("cost_center_code", line.CostCenterCode);
                await insertDetail.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertAuditLogAsync(
                connection,
                transaction,
                "JOURNAL",
                journalId,
                "SAVE_DRAFT",
                actor,
                $"journal_no={normalizedNo};company={header.CompanyId};location={header.LocationId};period={periodMonth:yyyy-MM};lines={normalizedLines.Count};debit={totalDebit};credit={totalCredit}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new AccessOperationResult(true, "Jurnal draft berhasil disimpan.", journalId);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            LogServiceWarning("SaveJournalDraftDuplicate", $"action=save_journal_draft status=duplicate journal_no={header.JournalNo} company_id={header.CompanyId} location_id={header.LocationId} period={periodMonth:yyyy-MM}", ex);
            return new AccessOperationResult(false, $"Nomor jurnal sudah digunakan pada perusahaan/lokasi/periode {periodMonth:yyyy-MM}.");
        }
        catch (Exception ex)
        {
            LogServiceError("SaveJournalDraftFailed", $"action=save_journal_draft status=failed journal_no={header.JournalNo} company_id={header.CompanyId} location_id={header.LocationId} period={periodMonth:yyyy-MM}", ex);
            return new AccessOperationResult(false, "Gagal menyimpan jurnal draft.");
        }
    }

    public async Task<AccessOperationResult> SubmitJournalAsync(
        long journalId,
        long companyId,
        long locationId,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureJournalSchemaAsync(cancellationToken);

        if (journalId <= 0 || companyId <= 0 || locationId <= 0)
        {
            return new AccessOperationResult(false, "Data jurnal tidak valid.");
        }

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
                AccountingModuleCode,
                AccountingSubmoduleTransactions,
                PermissionActionSubmit,
                companyId,
                locationId,
                cancellationToken,
                "Anda tidak memiliki izin untuk submit jurnal.");
            if (permissionFailure is not null)
            {
                return permissionFailure;
            }

            string? status = null;
            string? journalNo = null;
            DateTime periodMonth = GetPeriodMonthStart(DateTime.Today);
            await using (var lockCommand = new NpgsqlCommand(@"
SELECT status, journal_no, period_month
FROM gl_journal_headers
WHERE id = @id
  AND company_id = @company_id
  AND location_id = @location_id
FOR UPDATE;", connection, transaction))
            {
                lockCommand.Parameters.AddWithValue("id", journalId);
                lockCommand.Parameters.AddWithValue("company_id", companyId);
                lockCommand.Parameters.AddWithValue("location_id", locationId);
                await using var reader = await lockCommand.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    status = reader.GetString(0);
                    journalNo = reader.GetString(1);
                    periodMonth = reader.GetDateTime(2);
                }
            }

            if (string.IsNullOrWhiteSpace(status))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Data jurnal tidak ditemukan.");
            }

            if (!string.Equals(status, "DRAFT", StringComparison.OrdinalIgnoreCase))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Hanya jurnal DRAFT yang dapat di-submit.");
            }

            var isPeriodOpen = await IsAccountingPeriodOpenAsync(
                connection,
                transaction,
                companyId,
                locationId,
                periodMonth,
                forUpdate: true,
                cancellationToken);
            if (!isPeriodOpen)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(
                    false,
                    $"Periode akuntansi {periodMonth:yyyy-MM} sudah ditutup. Jurnal tidak dapat di-submit.");
            }

            await using (var update = new NpgsqlCommand(@"
UPDATE gl_journal_headers
SET status = 'SUBMITTED',
    updated_at = NOW()
WHERE id = @id;", connection, transaction))
            {
                update.Parameters.AddWithValue("id", journalId);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertAuditLogAsync(
                connection,
                transaction,
                "JOURNAL",
                journalId,
                "SUBMIT",
                actor,
                $"journal_no={journalNo};company={companyId};location={locationId}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new AccessOperationResult(true, "Jurnal berhasil di-submit untuk approval.", journalId);
        }
        catch (Exception ex)
        {
            LogServiceError("SubmitJournalFailed", $"action=submit_journal status=failed journal_id={journalId} company_id={companyId} location_id={locationId}", ex);
            return new AccessOperationResult(false, "Gagal submit jurnal.");
        }
    }

    public async Task<AccessOperationResult> ApproveJournalAsync(
        long journalId,
        long companyId,
        long locationId,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureJournalSchemaAsync(cancellationToken);

        if (journalId <= 0 || companyId <= 0 || locationId <= 0)
        {
            return new AccessOperationResult(false, "Data jurnal tidak valid.");
        }

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
                AccountingModuleCode,
                AccountingSubmoduleTransactions,
                PermissionActionApprove,
                companyId,
                locationId,
                cancellationToken,
                "Anda tidak memiliki izin approval jurnal.");
            if (permissionFailure is not null)
            {
                return permissionFailure;
            }

            string? status = null;
            string? journalNo = null;
            DateTime periodMonth = GetPeriodMonthStart(DateTime.Today);
            await using (var lockCommand = new NpgsqlCommand(@"
SELECT status, journal_no, period_month
FROM gl_journal_headers
WHERE id = @id
  AND company_id = @company_id
  AND location_id = @location_id
FOR UPDATE;", connection, transaction))
            {
                lockCommand.Parameters.AddWithValue("id", journalId);
                lockCommand.Parameters.AddWithValue("company_id", companyId);
                lockCommand.Parameters.AddWithValue("location_id", locationId);
                await using var reader = await lockCommand.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    status = reader.GetString(0);
                    journalNo = reader.GetString(1);
                    periodMonth = reader.GetDateTime(2);
                }
            }

            if (string.IsNullOrWhiteSpace(status))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Data jurnal tidak ditemukan.");
            }

            if (!string.Equals(status, "SUBMITTED", StringComparison.OrdinalIgnoreCase))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Hanya jurnal SUBMITTED yang dapat di-approve.");
            }

            var isPeriodOpen = await IsAccountingPeriodOpenAsync(
                connection,
                transaction,
                companyId,
                locationId,
                periodMonth,
                forUpdate: true,
                cancellationToken);
            if (!isPeriodOpen)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(
                    false,
                    $"Periode akuntansi {periodMonth:yyyy-MM} sudah ditutup. Jurnal tidak dapat di-approve.");
            }

            await using (var update = new NpgsqlCommand(@"
UPDATE gl_journal_headers
SET status = 'APPROVED',
    approved_at = NOW(),
    approved_by = @approved_by,
    updated_at = NOW()
WHERE id = @id;", connection, transaction))
            {
                update.Parameters.AddWithValue("id", journalId);
                update.Parameters.AddWithValue("approved_by", actor);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertAuditLogAsync(
                connection,
                transaction,
                "JOURNAL",
                journalId,
                "APPROVE",
                actor,
                $"journal_no={journalNo};company={companyId};location={locationId}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new AccessOperationResult(true, "Jurnal berhasil di-approve.", journalId);
        }
        catch (Exception ex)
        {
            LogServiceError("ApproveJournalFailed", $"action=approve_journal status=failed journal_id={journalId} company_id={companyId} location_id={locationId}", ex);
            return new AccessOperationResult(false, "Gagal approve jurnal.");
        }
    }

    public async Task<AccessOperationResult> PostJournalAsync(
        long journalId,
        long companyId,
        long locationId,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureJournalSchemaAsync(cancellationToken);

        if (journalId <= 0 || companyId <= 0 || locationId <= 0)
        {
            return new AccessOperationResult(false, "Data jurnal tidak valid.");
        }

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
                AccountingModuleCode,
                AccountingSubmoduleTransactions,
                PermissionActionPost,
                companyId,
                locationId,
                cancellationToken,
                "Anda tidak memiliki izin posting jurnal.");
            if (permissionFailure is not null)
            {
                return permissionFailure;
            }

            string? status = null;
            string? journalNo = null;
            DateTime journalDate = DateTime.Today;
            DateTime periodMonth = GetPeriodMonthStart(DateTime.Today);
            await using (var lockCommand = new NpgsqlCommand(@"
SELECT status, journal_no, journal_date, period_month
FROM gl_journal_headers
WHERE id = @id
  AND company_id = @company_id
  AND location_id = @location_id
FOR UPDATE;", connection, transaction))
            {
                lockCommand.Parameters.AddWithValue("id", journalId);
                lockCommand.Parameters.AddWithValue("company_id", companyId);
                lockCommand.Parameters.AddWithValue("location_id", locationId);

                await using var reader = await lockCommand.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    status = reader.GetString(0);
                    journalNo = reader.GetString(1);
                    journalDate = reader.GetDateTime(2);
                    periodMonth = reader.GetDateTime(3);
                }
            }

            if (string.IsNullOrWhiteSpace(status))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Data jurnal tidak ditemukan.");
            }

            if (!string.Equals(status, "APPROVED", StringComparison.OrdinalIgnoreCase))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Hanya jurnal APPROVED yang dapat diposting.");
            }

            var isPeriodOpen = await IsAccountingPeriodOpenAsync(
                connection,
                transaction,
                companyId,
                locationId,
                periodMonth,
                forUpdate: true,
                cancellationToken);
            if (!isPeriodOpen)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(
                    false,
                    $"Periode akuntansi {periodMonth:yyyy-MM} sudah ditutup. Jurnal tidak dapat diposting.");
            }

            await using (var deleteLedger = new NpgsqlCommand(@"
DELETE FROM gl_ledger_entries
WHERE journal_id = @journal_id;", connection, transaction))
            {
                deleteLedger.Parameters.AddWithValue("journal_id", journalId);
                await deleteLedger.ExecuteNonQueryAsync(cancellationToken);
            }

            int insertedLedgerRows;
            await using (var insertLedger = new NpgsqlCommand(@"
INSERT INTO gl_ledger_entries (
    company_id,
    location_id,
    period_month,
    journal_id,
    journal_no,
    journal_date,
    journal_line_no,
    account_id,
    debit,
    credit,
    description,
    department_code,
    project_code,
    cost_center_id,
    cost_center_code,
    posted_by,
    posted_at,
    created_at,
    updated_at)
SELECT h.company_id,
       h.location_id,
       @period_month,
       h.id,
       h.journal_no,
       h.journal_date,
       d.line_no,
       d.account_id,
       d.debit,
       d.credit,
       COALESCE(d.description, ''),
       COALESCE(d.department_code, ''),
       COALESCE(d.project_code, ''),
       d.cost_center_id,
       COALESCE(d.cost_center_code, ''),
       @posted_by,
       NOW(),
       NOW(),
       NOW()
FROM gl_journal_headers h
JOIN gl_journal_details d ON d.header_id = h.id
WHERE h.id = @journal_id
  AND h.company_id = @company_id
  AND h.location_id = @location_id;", connection, transaction))
            {
                insertLedger.Parameters.AddWithValue("period_month", periodMonth);
                insertLedger.Parameters.AddWithValue("posted_by", actor);
                insertLedger.Parameters.AddWithValue("journal_id", journalId);
                insertLedger.Parameters.AddWithValue("company_id", companyId);
                insertLedger.Parameters.AddWithValue("location_id", locationId);
                insertedLedgerRows = await insertLedger.ExecuteNonQueryAsync(cancellationToken);
            }

            if (insertedLedgerRows <= 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Detail jurnal tidak ditemukan. Posting dibatalkan.");
            }

            var equationSnapshot = await ComputeAccountingEquationSnapshotAsync(
                connection,
                transaction,
                companyId,
                locationId,
                periodMonth,
                cancellationToken);
            if (Math.Abs(equationSnapshot.EquationDifference) > 0.01m)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(
                    false,
                    $"Posting dibatalkan: selisih persamaan akuntansi {equationSnapshot.EquationDifference:N2}.");
            }

            await using (var update = new NpgsqlCommand(@"
UPDATE gl_journal_headers
SET status = 'POSTED',
    posted_at = NOW(),
    posted_by = @posted_by,
    updated_at = NOW()
WHERE id = @id;", connection, transaction))
            {
                update.Parameters.AddWithValue("id", journalId);
                update.Parameters.AddWithValue("posted_by", actor);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertAuditLogAsync(
                connection,
                transaction,
                "JOURNAL",
                journalId,
                "POST",
                actor,
                $"journal_no={journalNo};company={companyId};location={locationId};period={periodMonth:yyyy-MM};ledger_rows={insertedLedgerRows}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new AccessOperationResult(true, "Jurnal berhasil diposting.", journalId);
        }
        catch (Exception ex)
        {
            LogServiceError("PostJournalFailed", $"action=post_journal status=failed journal_id={journalId} company_id={companyId} location_id={locationId}", ex);
            return new AccessOperationResult(false, "Gagal memposting jurnal.");
        }
    }

    public async Task<List<ManagedJournalSummary>> SearchJournalsAsync(
        long companyId,
        long locationId,
        JournalSearchFilter filter,
        string actorUsername = "",
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureJournalSchemaAsync(cancellationToken);

        var output = new List<ManagedJournalSummary>();
        if (companyId <= 0 || locationId <= 0)
        {
            return output;
        }

        var safeFilter = filter ?? new JournalSearchFilter();

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        if (!await HasAnyPermissionAsync(
                connection,
                transaction: null,
                NormalizeActor(actorUsername),
                AccountingModuleCode,
                AccountingSubmoduleTransactions,
                AccountingTransactionsReadActions,
                companyId,
                locationId,
                cancellationToken))
        {
            return output;
        }

        await using var command = new NpgsqlCommand(@"
SELECT h.id,
       h.journal_no,
       h.journal_date,
       h.status,
       COALESCE(h.created_by, '')
FROM gl_journal_headers h
WHERE h.company_id = @company_id
  AND h.location_id = @location_id
  AND (@period_month IS NULL OR h.period_month = @period_month)
  AND (@date_from IS NULL OR h.journal_date >= @date_from)
  AND (@date_to IS NULL OR h.journal_date <= @date_to)
  AND (
      @status_filter = ''
      OR (@status_filter = 'UNPOSTED' AND upper(h.status) IN ('DRAFT', 'SUBMITTED', 'APPROVED'))
      OR upper(h.status) = @status_filter)
  AND (
      @keyword = ''
      OR h.journal_no ILIKE @keyword_like
      OR COALESCE(h.reference_no, '') ILIKE @keyword_like
      OR COALESCE(h.description, '') ILIKE @keyword_like)
ORDER BY h.journal_date DESC, h.id DESC
LIMIT 500;", connection);

        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);
        command.Parameters.Add(new NpgsqlParameter("period_month", NpgsqlDbType.Date)
        {
            Value = safeFilter.PeriodMonth?.Date ?? (object)DBNull.Value
        });
        command.Parameters.Add(new NpgsqlParameter("date_from", NpgsqlDbType.Date)
        {
            Value = safeFilter.DateFrom?.Date ?? (object)DBNull.Value
        });
        command.Parameters.Add(new NpgsqlParameter("date_to", NpgsqlDbType.Date)
        {
            Value = safeFilter.DateTo?.Date ?? (object)DBNull.Value
        });

        var statusFilter = (safeFilter.Status ?? string.Empty).Trim().ToUpperInvariant();
        command.Parameters.AddWithValue("status_filter", statusFilter);

        var keyword = (safeFilter.Keyword ?? string.Empty).Trim();
        command.Parameters.AddWithValue("keyword", keyword);
        command.Parameters.AddWithValue("keyword_like", $"%{keyword}%");

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(new ManagedJournalSummary
            {
                Id = reader.GetInt64(0),
                JournalNo = reader.GetString(1),
                JournalDate = reader.GetDateTime(2),
                Status = reader.GetString(3),
                CreatedBy = reader.GetString(4)
            });
        }

        return output;
    }

    private static string NormalizeCostCenterCode(string? costCenterCode)
    {
        return string.IsNullOrWhiteSpace(costCenterCode)
            ? string.Empty
            : costCenterCode.Trim().ToUpperInvariant();
    }
}


