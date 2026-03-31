using Npgsql;

namespace Accounting.Services;

public sealed partial class PostgresAccessControlService
{
    public async Task<EstateHierarchyWorkspace> GetEstateHierarchyAsync(
        long companyId,
        long locationId,
        bool includeInactive = false,
        string actorUsername = "",
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureJournalSchemaAsync(cancellationToken);

        var workspace = new EstateHierarchyWorkspace();
        if (companyId <= 0 || locationId <= 0)
        {
            return workspace;
        }

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        if (!await HasScopeAccessAsync(connection, null, NormalizeActor(actorUsername), companyId, locationId, cancellationToken))
        {
            return workspace;
        }

        var estatesById = new Dictionary<long, ManagedEstate>();

        await using (var estateCommand = new NpgsqlCommand(@"
SELECT id,
       company_id,
       location_id,
       upper(btrim(code)) AS code,
       btrim(name) AS name,
       coalesce(is_active, FALSE) AS is_active
FROM estates
WHERE company_id = @company_id
  AND location_id = @location_id
  AND (@include_inactive = TRUE OR coalesce(is_active, FALSE) = TRUE)
ORDER BY upper(btrim(code));", connection))
        {
            estateCommand.Parameters.AddWithValue("company_id", companyId);
            estateCommand.Parameters.AddWithValue("location_id", locationId);
            estateCommand.Parameters.AddWithValue("include_inactive", includeInactive);

            await using var reader = await estateCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var estate = new ManagedEstate
                {
                    Id = reader.GetInt64(0),
                    CompanyId = reader.GetInt64(1),
                    LocationId = reader.GetInt64(2),
                    Code = reader.GetString(3),
                    Name = reader.GetString(4),
                    IsActive = !reader.IsDBNull(5) && reader.GetBoolean(5)
                };
                estatesById[estate.Id] = estate;
                workspace.Estates.Add(estate);
            }
        }

        var divisionsById = new Dictionary<long, ManagedDivision>();

        await using (var divisionCommand = new NpgsqlCommand(@"
SELECT d.id,
       d.estate_id,
       upper(btrim(d.code)) AS division_code,
       btrim(d.name) AS division_name,
       coalesce(d.is_active, FALSE) AS is_active,
       upper(btrim(e.code)) AS estate_code,
       btrim(e.name) AS estate_name
FROM divisions d
JOIN estates e ON e.id = d.estate_id
WHERE e.company_id = @company_id
  AND e.location_id = @location_id
  AND (@include_inactive = TRUE OR (coalesce(e.is_active, FALSE) = TRUE AND coalesce(d.is_active, FALSE) = TRUE))
ORDER BY upper(btrim(e.code)), upper(btrim(d.code));", connection))
        {
            divisionCommand.Parameters.AddWithValue("company_id", companyId);
            divisionCommand.Parameters.AddWithValue("location_id", locationId);
            divisionCommand.Parameters.AddWithValue("include_inactive", includeInactive);

            await using var reader = await divisionCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var division = new ManagedDivision
                {
                    Id = reader.GetInt64(0),
                    EstateId = reader.GetInt64(1),
                    CompanyId = companyId,
                    LocationId = locationId,
                    Code = reader.GetString(2),
                    Name = reader.GetString(3),
                    IsActive = !reader.IsDBNull(4) && reader.GetBoolean(4),
                    EstateCode = reader.GetString(5),
                    EstateName = reader.GetString(6)
                };
                divisionsById[division.Id] = division;
                if (estatesById.TryGetValue(division.EstateId, out var estate))
                {
                    estate.Divisions.Add(division);
                }
            }
        }

        await using (var blockCommand = new NpgsqlCommand(@"
SELECT b.id,
       b.division_id,
       d.estate_id,
       upper(btrim(b.code)) AS block_code,
       btrim(b.name) AS block_name,
       coalesce(b.is_active, FALSE) AS is_active,
       upper(btrim(d.code)) AS division_code,
       btrim(d.name) AS division_name,
       upper(btrim(e.code)) AS estate_code,
       btrim(e.name) AS estate_name
FROM blocks b
JOIN divisions d ON d.id = b.division_id
JOIN estates e ON e.id = d.estate_id
WHERE e.company_id = @company_id
  AND e.location_id = @location_id
  AND (@include_inactive = TRUE OR (coalesce(e.is_active, FALSE) = TRUE AND coalesce(d.is_active, FALSE) = TRUE AND coalesce(b.is_active, FALSE) = TRUE))
ORDER BY upper(btrim(e.code)), upper(btrim(d.code)), upper(btrim(b.code));", connection))
        {
            blockCommand.Parameters.AddWithValue("company_id", companyId);
            blockCommand.Parameters.AddWithValue("location_id", locationId);
            blockCommand.Parameters.AddWithValue("include_inactive", includeInactive);

            await using var reader = await blockCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var block = new ManagedBlock
                {
                    Id = reader.GetInt64(0),
                    DivisionId = reader.GetInt64(1),
                    EstateId = reader.GetInt64(2),
                    CompanyId = companyId,
                    LocationId = locationId,
                    Code = reader.GetString(3),
                    Name = reader.GetString(4),
                    IsActive = !reader.IsDBNull(5) && reader.GetBoolean(5),
                    DivisionCode = reader.GetString(6),
                    DivisionName = reader.GetString(7),
                    EstateCode = reader.GetString(8),
                    EstateName = reader.GetString(9)
                };

                if (divisionsById.TryGetValue(block.DivisionId, out var division))
                {
                    division.Blocks.Add(block);
                }
            }
        }

        return workspace;
    }

    public async Task<AccessOperationResult> SaveEstateAsync(
        long companyId,
        long locationId,
        ManagedEstate estate,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        return await SaveEstateAsyncInternal(companyId, locationId, estate, actorUsername, null, null, cancellationToken);
    }

    public async Task<AccessOperationResult> SaveDivisionAsync(
        long companyId,
        long locationId,
        ManagedDivision division,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        return await SaveDivisionAsyncInternal(companyId, locationId, division, actorUsername, null, null, cancellationToken);
    }

    public async Task<AccessOperationResult> SaveBlockAsync(
        long companyId,
        long locationId,
        ManagedBlock block,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        return await SaveBlockAsyncInternal(companyId, locationId, block, actorUsername, null, null, cancellationToken);
    }

    public async Task<AccessOperationResult> SoftDeleteEstateAsync(long companyId, long locationId, long estateId, string actorUsername, CancellationToken cancellationToken = default)
    {
        return await SoftDeleteHierarchyRecordAsync(companyId, locationId, estateId, actorUsername, "ESTATE", cancellationToken);
    }

    public async Task<AccessOperationResult> SoftDeleteDivisionAsync(long companyId, long locationId, long divisionId, string actorUsername, CancellationToken cancellationToken = default)
    {
        return await SoftDeleteHierarchyRecordAsync(companyId, locationId, divisionId, actorUsername, "DIVISION", cancellationToken);
    }

    public async Task<AccessOperationResult> SoftDeleteBlockAsync(long companyId, long locationId, long blockId, string actorUsername, CancellationToken cancellationToken = default)
    {
        return await SoftDeleteHierarchyRecordAsync(companyId, locationId, blockId, actorUsername, "BLOCK", cancellationToken);
    }

    public async Task<EstateHierarchyImportExecutionResult> ImportEstateHierarchyAsync(
        long companyId,
        long locationId,
        EstateHierarchyImportBundle bundle,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureJournalSchemaAsync(cancellationToken);

        if (companyId <= 0 || locationId <= 0)
        {
            return new EstateHierarchyImportExecutionResult { IsSuccess = false, Message = "Perusahaan/lokasi estate hierarchy tidak valid." };
        }

        var errors = new List<InventoryImportError>();
        var importedEstateCount = 0;
        var importedDivisionCount = 0;
        var importedBlockCount = 0;
        var actor = NormalizeActor(actorUsername);

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var permissionFailure = await EnsurePermissionAsync(
            connection,
            transaction,
            actor,
            AccountingModuleCode,
            AccountingSubmoduleMasterData,
            PermissionActionImportMasterData,
            companyId,
            locationId,
            cancellationToken,
            "Anda tidak memiliki izin untuk import master estate/division/blok.");
        if (permissionFailure is not null)
        {
            return new EstateHierarchyImportExecutionResult { IsSuccess = false, Message = permissionFailure.Message };
        }

        foreach (var estateRow in bundle.Estates.OrderBy(x => x.Code, StringComparer.OrdinalIgnoreCase))
        {
            var result = await SaveEstateAsyncInternal(
                companyId,
                locationId,
                new ManagedEstate { Id = 0, CompanyId = companyId, LocationId = locationId, Code = estateRow.Code, Name = estateRow.Name, IsActive = estateRow.IsActive },
                actor,
                connection,
                transaction,
                cancellationToken);
            if (result.IsSuccess)
            {
                importedEstateCount++;
            }
            else
            {
                errors.Add(new InventoryImportError { SheetName = "Estates", RowNumber = estateRow.RowNumber, Message = result.Message });
            }
        }

        foreach (var divisionRow in bundle.Divisions.OrderBy(x => x.EstateCode, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Code, StringComparer.OrdinalIgnoreCase))
        {
            var result = await SaveDivisionAsyncInternal(
                companyId,
                locationId,
                new ManagedDivision { Id = 0, CompanyId = companyId, LocationId = locationId, EstateCode = divisionRow.EstateCode, Code = divisionRow.Code, Name = divisionRow.Name, IsActive = divisionRow.IsActive },
                actor,
                connection,
                transaction,
                cancellationToken);
            if (result.IsSuccess)
            {
                importedDivisionCount++;
            }
            else
            {
                errors.Add(new InventoryImportError { SheetName = "Divisions", RowNumber = divisionRow.RowNumber, Message = result.Message });
            }
        }

        foreach (var blockRow in bundle.Blocks.OrderBy(x => x.EstateCode, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.DivisionCode, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Code, StringComparer.OrdinalIgnoreCase))
        {
            var result = await SaveBlockAsyncInternal(
                companyId,
                locationId,
                new ManagedBlock { Id = 0, CompanyId = companyId, LocationId = locationId, EstateCode = blockRow.EstateCode, DivisionCode = blockRow.DivisionCode, Code = blockRow.Code, Name = blockRow.Name, IsActive = blockRow.IsActive },
                actor,
                connection,
                transaction,
                cancellationToken);
            if (result.IsSuccess)
            {
                importedBlockCount++;
            }
            else
            {
                errors.Add(new InventoryImportError { SheetName = "Blocks", RowNumber = blockRow.RowNumber, Message = result.Message });
            }
        }

        if (errors.Count > 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return new EstateHierarchyImportExecutionResult
            {
                IsSuccess = false,
                Message = $"Import estate/division/blok selesai dengan {errors.Count} error.",
                ImportedEstateCount = importedEstateCount,
                ImportedDivisionCount = importedDivisionCount,
                ImportedBlockCount = importedBlockCount,
                Errors = errors
            };
        }

        await transaction.CommitAsync(cancellationToken);
        return new EstateHierarchyImportExecutionResult
        {
            IsSuccess = true,
            Message = $"Import estate/division/blok berhasil. Estate {importedEstateCount}, Divisi {importedDivisionCount}, Blok {importedBlockCount}.",
            ImportedEstateCount = importedEstateCount,
            ImportedDivisionCount = importedDivisionCount,
            ImportedBlockCount = importedBlockCount
        };
    }

    private async Task<AccessOperationResult> SaveEstateAsyncInternal(
        long companyId,
        long locationId,
        ManagedEstate estate,
        string actorUsername,
        NpgsqlConnection? existingConnection,
        NpgsqlTransaction? existingTransaction,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureJournalSchemaAsync(cancellationToken);

        var code = NormalizeDimensionCode(estate.Code);
        var name = NormalizeDimensionName(estate.Name);
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name))
        {
            return new AccessOperationResult(false, "Kode dan nama estate wajib diisi.");
        }

        var ownsConnection = existingConnection is null;
        await using var connection = ownsConnection ? new NpgsqlConnection(_options.ConnectionString) : null;
        var dbConnection = existingConnection ?? connection!;
        if (ownsConnection)
        {
            await dbConnection.OpenAsync(cancellationToken);
        }

        var ownsTransaction = existingTransaction is null;
        await using var transaction = ownsTransaction ? await dbConnection.BeginTransactionAsync(cancellationToken) : null;
        var dbTransaction = existingTransaction ?? transaction!;

        try
        {
            if (ownsTransaction)
            {
                var permissionFailure = await EnsurePermissionAsync(
                    dbConnection,
                    dbTransaction,
                    NormalizeActor(actorUsername),
                    AccountingModuleCode,
                    AccountingSubmoduleMasterData,
                    ResolveWriteAction(estate.Id),
                    companyId,
                    locationId,
                    cancellationToken,
                    "Anda tidak memiliki izin untuk mengelola master estate.");
                if (permissionFailure is not null)
                {
                    return permissionFailure;
                }
            }

            long entityId;
            if (estate.Id > 0)
            {
                await using var update = new NpgsqlCommand(@"
UPDATE estates
SET code = @code,
    name = @name,
    is_active = @is_active,
    updated_by = @actor,
    updated_at = NOW()
WHERE id = @id
  AND company_id = @company_id
  AND location_id = @location_id;", dbConnection, dbTransaction);
                update.Parameters.AddWithValue("id", estate.Id);
                update.Parameters.AddWithValue("company_id", companyId);
                update.Parameters.AddWithValue("location_id", locationId);
                update.Parameters.AddWithValue("code", code);
                update.Parameters.AddWithValue("name", name);
                update.Parameters.AddWithValue("is_active", estate.IsActive);
                update.Parameters.AddWithValue("actor", NormalizeActor(actorUsername));
                if (await update.ExecuteNonQueryAsync(cancellationToken) <= 0)
                {
                    if (ownsTransaction)
                    {
                        await dbTransaction.RollbackAsync(cancellationToken);
                    }
                    return new AccessOperationResult(false, "Estate tidak ditemukan.");
                }

                entityId = estate.Id;
            }
            else
            {
                await using var upsert = new NpgsqlCommand(@"
INSERT INTO estates (company_id, location_id, code, name, is_active, created_by, updated_by, created_at, updated_at)
VALUES (@company_id, @location_id, @code, @name, @is_active, @actor, @actor, NOW(), NOW())
ON CONFLICT (company_id, location_id, code) DO UPDATE
SET name = EXCLUDED.name,
    is_active = EXCLUDED.is_active,
    updated_by = EXCLUDED.updated_by,
    updated_at = NOW()
RETURNING id;", dbConnection, dbTransaction);
                upsert.Parameters.AddWithValue("company_id", companyId);
                upsert.Parameters.AddWithValue("location_id", locationId);
                upsert.Parameters.AddWithValue("code", code);
                upsert.Parameters.AddWithValue("name", name);
                upsert.Parameters.AddWithValue("is_active", estate.IsActive);
                upsert.Parameters.AddWithValue("actor", NormalizeActor(actorUsername));
                entityId = Convert.ToInt64(await upsert.ExecuteScalarAsync(cancellationToken));
            }

            await InsertAuditLogAsync(dbConnection, dbTransaction, "ESTATE", entityId, estate.Id > 0 ? "UPDATE" : "CREATE_OR_UPDATE", NormalizeActor(actorUsername), $"company={companyId};location={locationId};code={code};active={estate.IsActive}", cancellationToken);
            if (ownsTransaction)
            {
                await dbTransaction.CommitAsync(cancellationToken);
            }

            return new AccessOperationResult(true, "Estate berhasil disimpan.", entityId);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return new AccessOperationResult(false, "Kode estate sudah digunakan.");
        }
    }

    private async Task<AccessOperationResult> SaveDivisionAsyncInternal(
        long companyId,
        long locationId,
        ManagedDivision division,
        string actorUsername,
        NpgsqlConnection? existingConnection,
        NpgsqlTransaction? existingTransaction,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureJournalSchemaAsync(cancellationToken);

        var estateCode = NormalizeDimensionCode(division.EstateCode);
        var code = NormalizeDimensionCode(division.Code);
        var name = NormalizeDimensionName(division.Name);
        if (string.IsNullOrWhiteSpace(estateCode) || string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name))
        {
            return new AccessOperationResult(false, "EstateCode, kode, dan nama divisi wajib diisi.");
        }

        var ownsConnection = existingConnection is null;
        await using var connection = ownsConnection ? new NpgsqlConnection(_options.ConnectionString) : null;
        var dbConnection = existingConnection ?? connection!;
        if (ownsConnection)
        {
            await dbConnection.OpenAsync(cancellationToken);
        }

        var ownsTransaction = existingTransaction is null;
        await using var transaction = ownsTransaction ? await dbConnection.BeginTransactionAsync(cancellationToken) : null;
        var dbTransaction = existingTransaction ?? transaction!;

        try
        {
            if (ownsTransaction)
            {
                var permissionFailure = await EnsurePermissionAsync(dbConnection, dbTransaction, NormalizeActor(actorUsername), AccountingModuleCode, AccountingSubmoduleMasterData, ResolveWriteAction(division.Id), companyId, locationId, cancellationToken, "Anda tidak memiliki izin untuk mengelola master divisi.");
                if (permissionFailure is not null)
                {
                    return permissionFailure;
                }
            }

            var parentEstate = await FindEstateRecordAsync(dbConnection, dbTransaction, companyId, locationId, estateCode, cancellationToken);
            if (!parentEstate.HasValue)
            {
                if (ownsTransaction) await dbTransaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, $"Estate '{estateCode}' belum ada.");
            }

            if (!parentEstate.Value.IsActive)
            {
                if (ownsTransaction) await dbTransaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, $"Estate '{estateCode}' nonaktif.");
            }

            long entityId;
            if (division.Id > 0)
            {
                await using var update = new NpgsqlCommand(@"
UPDATE divisions
SET estate_id = @estate_id,
    code = @code,
    name = @name,
    is_active = @is_active,
    updated_by = @actor,
    updated_at = NOW()
WHERE id = @id;", dbConnection, dbTransaction);
                update.Parameters.AddWithValue("id", division.Id);
                update.Parameters.AddWithValue("estate_id", parentEstate.Value.Id);
                update.Parameters.AddWithValue("code", code);
                update.Parameters.AddWithValue("name", name);
                update.Parameters.AddWithValue("is_active", division.IsActive);
                update.Parameters.AddWithValue("actor", NormalizeActor(actorUsername));
                if (await update.ExecuteNonQueryAsync(cancellationToken) <= 0)
                {
                    if (ownsTransaction) await dbTransaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(false, "Divisi tidak ditemukan.");
                }

                entityId = division.Id;
            }
            else
            {
                await using var upsert = new NpgsqlCommand(@"
INSERT INTO divisions (estate_id, code, name, is_active, created_by, updated_by, created_at, updated_at)
VALUES (@estate_id, @code, @name, @is_active, @actor, @actor, NOW(), NOW())
ON CONFLICT (estate_id, code) DO UPDATE
SET name = EXCLUDED.name,
    is_active = EXCLUDED.is_active,
    updated_by = EXCLUDED.updated_by,
    updated_at = NOW()
RETURNING id;", dbConnection, dbTransaction);
                upsert.Parameters.AddWithValue("estate_id", parentEstate.Value.Id);
                upsert.Parameters.AddWithValue("code", code);
                upsert.Parameters.AddWithValue("name", name);
                upsert.Parameters.AddWithValue("is_active", division.IsActive);
                upsert.Parameters.AddWithValue("actor", NormalizeActor(actorUsername));
                entityId = Convert.ToInt64(await upsert.ExecuteScalarAsync(cancellationToken));
            }

            await InsertAuditLogAsync(dbConnection, dbTransaction, "DIVISION", entityId, division.Id > 0 ? "UPDATE" : "CREATE_OR_UPDATE", NormalizeActor(actorUsername), $"company={companyId};location={locationId};estate={estateCode};code={code};active={division.IsActive}", cancellationToken);
            if (ownsTransaction) await dbTransaction.CommitAsync(cancellationToken);
            return new AccessOperationResult(true, "Divisi berhasil disimpan.", entityId);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return new AccessOperationResult(false, "Kode divisi sudah digunakan pada estate tersebut.");
        }
    }

    private async Task<AccessOperationResult> SaveBlockAsyncInternal(
        long companyId,
        long locationId,
        ManagedBlock block,
        string actorUsername,
        NpgsqlConnection? existingConnection,
        NpgsqlTransaction? existingTransaction,
        CancellationToken cancellationToken)
    {
        var estateCode = NormalizeDimensionCode(block.EstateCode);
        var divisionCode = NormalizeDimensionCode(block.DivisionCode);
        var code = NormalizeDimensionCode(block.Code);
        var name = NormalizeDimensionName(block.Name);
        if (string.IsNullOrWhiteSpace(estateCode) || string.IsNullOrWhiteSpace(divisionCode) || string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name))
        {
            return new AccessOperationResult(false, "EstateCode, DivisionCode, kode, dan nama blok wajib diisi.");
        }

        var ownsConnection = existingConnection is null;
        await using var connection = ownsConnection ? new NpgsqlConnection(_options.ConnectionString) : null;
        var dbConnection = existingConnection ?? connection!;
        if (ownsConnection)
        {
            await dbConnection.OpenAsync(cancellationToken);
        }

        var ownsTransaction = existingTransaction is null;
        await using var transaction = ownsTransaction ? await dbConnection.BeginTransactionAsync(cancellationToken) : null;
        var dbTransaction = existingTransaction ?? transaction!;

        try
        {
            if (ownsTransaction)
            {
                var permissionFailure = await EnsurePermissionAsync(dbConnection, dbTransaction, NormalizeActor(actorUsername), AccountingModuleCode, AccountingSubmoduleMasterData, ResolveWriteAction(block.Id), companyId, locationId, cancellationToken, "Anda tidak memiliki izin untuk mengelola master blok.");
                if (permissionFailure is not null)
                {
                    return permissionFailure;
                }
            }

            var parentDivision = await FindDivisionRecordAsync(dbConnection, dbTransaction, companyId, locationId, estateCode, divisionCode, cancellationToken);
            if (!parentDivision.HasValue)
            {
                if (ownsTransaction) await dbTransaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, $"Divisi '{estateCode}-{divisionCode}' belum ada.");
            }

            if (!parentDivision.Value.IsActive)
            {
                if (ownsTransaction) await dbTransaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, $"Divisi '{estateCode}-{divisionCode}' nonaktif.");
            }

            long entityId;
            if (block.Id > 0)
            {
                await using var update = new NpgsqlCommand(@"
UPDATE blocks
SET division_id = @division_id,
    code = @code,
    name = @name,
    is_active = @is_active,
    updated_by = @actor,
    updated_at = NOW()
WHERE id = @id;", dbConnection, dbTransaction);
                update.Parameters.AddWithValue("id", block.Id);
                update.Parameters.AddWithValue("division_id", parentDivision.Value.Id);
                update.Parameters.AddWithValue("code", code);
                update.Parameters.AddWithValue("name", name);
                update.Parameters.AddWithValue("is_active", block.IsActive);
                update.Parameters.AddWithValue("actor", NormalizeActor(actorUsername));
                if (await update.ExecuteNonQueryAsync(cancellationToken) <= 0)
                {
                    if (ownsTransaction) await dbTransaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(false, "Blok tidak ditemukan.");
                }

                entityId = block.Id;
            }
            else
            {
                await using var upsert = new NpgsqlCommand(@"
INSERT INTO blocks (division_id, code, name, is_active, created_by, updated_by, created_at, updated_at)
VALUES (@division_id, @code, @name, @is_active, @actor, @actor, NOW(), NOW())
ON CONFLICT (division_id, code) DO UPDATE
SET name = EXCLUDED.name,
    is_active = EXCLUDED.is_active,
    updated_by = EXCLUDED.updated_by,
    updated_at = NOW()
RETURNING id;", dbConnection, dbTransaction);
                upsert.Parameters.AddWithValue("division_id", parentDivision.Value.Id);
                upsert.Parameters.AddWithValue("code", code);
                upsert.Parameters.AddWithValue("name", name);
                upsert.Parameters.AddWithValue("is_active", block.IsActive);
                upsert.Parameters.AddWithValue("actor", NormalizeActor(actorUsername));
                entityId = Convert.ToInt64(await upsert.ExecuteScalarAsync(cancellationToken));
            }

            await InsertAuditLogAsync(dbConnection, dbTransaction, "BLOCK", entityId, block.Id > 0 ? "UPDATE" : "CREATE_OR_UPDATE", NormalizeActor(actorUsername), $"company={companyId};location={locationId};estate={estateCode};division={divisionCode};code={code};active={block.IsActive}", cancellationToken);
            if (ownsTransaction) await dbTransaction.CommitAsync(cancellationToken);
            return new AccessOperationResult(true, "Blok berhasil disimpan.", entityId);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return new AccessOperationResult(false, "Kode blok sudah digunakan pada divisi tersebut.");
        }
    }

    private async Task<AccessOperationResult> SoftDeleteHierarchyRecordAsync(
        long companyId,
        long locationId,
        long entityId,
        string actorUsername,
        string level,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureJournalSchemaAsync(cancellationToken);

        if (companyId <= 0 || locationId <= 0 || entityId <= 0)
        {
            return new AccessOperationResult(false, "Data hierarchy tidak valid.");
        }

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var permissionFailure = await EnsurePermissionAsync(connection, transaction, NormalizeActor(actorUsername), AccountingModuleCode, AccountingSubmoduleMasterData, PermissionActionDelete, companyId, locationId, cancellationToken, "Anda tidak memiliki izin untuk menonaktifkan hierarchy estate/division/blok.");
        if (permissionFailure is not null)
        {
            return permissionFailure;
        }

        var (tableName, idColumn, childSql, codeSql, updateSql, entityType, successMessage) = level switch
        {
            "ESTATE" => ("estates", "id", "SELECT COUNT(1) FROM divisions WHERE estate_id = @id AND coalesce(is_active, FALSE) = TRUE;", "SELECT upper(btrim(code)) FROM estates WHERE id = @id AND company_id = @company_id AND location_id = @location_id;", "UPDATE estates SET is_active = FALSE, updated_by = @actor, updated_at = NOW() WHERE id = @id AND company_id = @company_id AND location_id = @location_id;", "ESTATE", "Estate berhasil dinonaktifkan."),
            "DIVISION" => ("divisions", "id", "SELECT COUNT(1) FROM blocks WHERE division_id = @id AND coalesce(is_active, FALSE) = TRUE;", "SELECT upper(btrim(d.code)) FROM divisions d JOIN estates e ON e.id = d.estate_id WHERE d.id = @id AND e.company_id = @company_id AND e.location_id = @location_id;", "UPDATE divisions SET is_active = FALSE, updated_by = @actor, updated_at = NOW() WHERE id = @id;", "DIVISION", "Divisi berhasil dinonaktifkan."),
            _ => ("blocks", "id", "SELECT COUNT(1) FROM gl_journal_details WHERE block_id = @id;", "SELECT upper(btrim(b.code)) FROM blocks b JOIN divisions d ON d.id = b.division_id JOIN estates e ON e.id = d.estate_id WHERE b.id = @id AND e.company_id = @company_id AND e.location_id = @location_id;", "UPDATE blocks SET is_active = FALSE, updated_by = @actor, updated_at = NOW() WHERE id = @id;", "BLOCK", "Blok berhasil dinonaktifkan.")
        };

        string? code = null;
        await using (var codeCommand = new NpgsqlCommand(codeSql, connection, transaction))
        {
            codeCommand.Parameters.AddWithValue("id", entityId);
            codeCommand.Parameters.AddWithValue("company_id", companyId);
            codeCommand.Parameters.AddWithValue("location_id", locationId);
            code = Convert.ToString(await codeCommand.ExecuteScalarAsync(cancellationToken));
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AccessOperationResult(false, $"{level} tidak ditemukan.");
        }

        if (level != "BLOCK")
        {
            await using var childCommand = new NpgsqlCommand(childSql, connection, transaction);
            childCommand.Parameters.AddWithValue("id", entityId);
            var childCount = Convert.ToInt32(await childCommand.ExecuteScalarAsync(cancellationToken));
            if (childCount > 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, $"{level} masih memiliki child aktif.");
            }
        }

        await using (var updateCommand = new NpgsqlCommand(updateSql, connection, transaction))
        {
            updateCommand.Parameters.AddWithValue("id", entityId);
            updateCommand.Parameters.AddWithValue("company_id", companyId);
            updateCommand.Parameters.AddWithValue("location_id", locationId);
            updateCommand.Parameters.AddWithValue("actor", NormalizeActor(actorUsername));
            if (await updateCommand.ExecuteNonQueryAsync(cancellationToken) <= 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, $"{level} tidak ditemukan.");
            }
        }

        await InsertAuditLogAsync(connection, transaction, entityType, entityId, "DEACTIVATE", NormalizeActor(actorUsername), $"company={companyId};location={locationId};code={code}", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AccessOperationResult(true, successMessage, entityId);
    }

    private static async Task<(long Id, bool IsActive)?> FindEstateRecordAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long companyId, long locationId, string estateCode, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(@"
SELECT id, coalesce(is_active, FALSE)
FROM estates
WHERE company_id = @company_id
  AND location_id = @location_id
  AND upper(btrim(code)) = @code
LIMIT 1;", connection, transaction);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);
        command.Parameters.AddWithValue("code", estateCode);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return (reader.GetInt64(0), !reader.IsDBNull(1) && reader.GetBoolean(1));
        }

        return null;
    }

    private static async Task<(long Id, bool IsActive)?> FindDivisionRecordAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long companyId, long locationId, string estateCode, string divisionCode, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(@"
SELECT d.id,
       coalesce(e.is_active, FALSE) AND coalesce(d.is_active, FALSE)
FROM divisions d
JOIN estates e ON e.id = d.estate_id
WHERE e.company_id = @company_id
  AND e.location_id = @location_id
  AND upper(btrim(e.code)) = @estate_code
  AND upper(btrim(d.code)) = @division_code
LIMIT 1;", connection, transaction);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);
        command.Parameters.AddWithValue("estate_code", estateCode);
        command.Parameters.AddWithValue("division_code", divisionCode);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return (reader.GetInt64(0), !reader.IsDBNull(1) && reader.GetBoolean(1));
        }

        return null;
    }
}
