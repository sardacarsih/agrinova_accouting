using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Accounting.Services;

public sealed partial class PostgresAccessControlService
{
    private const string InventoryCentralBaseUrlSettingKey = "inventory_central_base_url";
    private const string InventoryCentralApiKeySettingKey = "inventory_central_api_key";
    private const string InventoryCentralUploadPathSettingKey = "inventory_central_upload_path";
    private const string InventoryCentralDownloadPathSettingKey = "inventory_central_download_path";
    private const string InventoryCentralTimeoutSettingKey = "inventory_central_timeout_seconds";

    private const string InventorySyncDirectionUpload = "UPLOAD";
    private const string InventorySyncDirectionDownload = "DOWNLOAD";
    private const string InventorySyncTriggerManual = "MANUAL";
    private const string InventorySyncStatusRunning = "RUNNING";
    private const string InventorySyncStatusSuccess = "SUCCESS";
    private const string InventorySyncStatusPartial = "PARTIAL";
    private const string InventorySyncStatusFailed = "FAILED";

    private static readonly JsonSerializerOptions CentralSyncJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<InventoryCentralSyncSettings> GetInventoryCentralSyncSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return await GetInventoryCentralSyncSettingsInternalAsync(connection, null, cancellationToken);
    }

    public async Task<AccessOperationResult> SaveInventoryCentralSyncSettingsAsync(
        InventoryCentralSyncSettings settings,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        if (settings is null)
        {
            return new AccessOperationResult(false, "Pengaturan sync pusat tidak valid.");
        }

        var normalizedBaseUrl = (settings.BaseUrl ?? string.Empty).Trim();
        var normalizedApiKey = (settings.ApiKey ?? string.Empty).Trim();
        var normalizedUploadPath = NormalizeCentralPath(settings.UploadPath, "/api/inventory/sync/upload");
        var normalizedDownloadPath = NormalizeCentralPath(settings.DownloadPath, "/api/inventory/sync/download");
        var normalizedTimeout = settings.TimeoutSeconds > 0 ? settings.TimeoutSeconds : 30;

        if (!string.IsNullOrWhiteSpace(normalizedBaseUrl) &&
            !IsValidBaseUrlOrConnectionString(normalizedBaseUrl))
        {
            return new AccessOperationResult(false, "Base URL/connection string sync pusat tidak valid.");
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
                InventoryModuleCode,
                InventorySubmoduleApiInv,
                PermissionActionUpdateSettings,
                null,
                null,
                cancellationToken,
                "Anda tidak memiliki izin untuk mengubah pengaturan sync pusat.");
            if (permissionFailure is not null)
            {
                return permissionFailure;
            }

            await UpsertSystemSettingValueAsync(connection, transaction, InventoryCentralBaseUrlSettingKey, normalizedBaseUrl, actor, cancellationToken);
            await UpsertSystemSettingValueAsync(connection, transaction, InventoryCentralApiKeySettingKey, normalizedApiKey, actor, cancellationToken);
            await UpsertSystemSettingValueAsync(connection, transaction, InventoryCentralUploadPathSettingKey, normalizedUploadPath, actor, cancellationToken);
            await UpsertSystemSettingValueAsync(connection, transaction, InventoryCentralDownloadPathSettingKey, normalizedDownloadPath, actor, cancellationToken);
            await UpsertSystemSettingValueAsync(connection, transaction, InventoryCentralTimeoutSettingKey, normalizedTimeout.ToString(CultureInfo.InvariantCulture), actor, cancellationToken);

            await InsertAuditLogAsync(
                connection,
                transaction,
                "INVENTORY_SETTING",
                0,
                "SAVE_CENTRAL_SYNC_SETTING",
                actor,
                $"base_url={normalizedBaseUrl};upload_path={normalizedUploadPath};download_path={normalizedDownloadPath};timeout={normalizedTimeout}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new AccessOperationResult(true, "Pengaturan sync pusat berhasil disimpan.");
        }
        catch (Exception ex)
        {
            return new AccessOperationResult(false, $"Gagal menyimpan pengaturan sync pusat: {ex.Message}");
        }
    }

    public async Task<AccessOperationResult> UploadInventoryToCentralAsync(
        long companyId,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        if (companyId <= 0)
        {
            return new AccessOperationResult(false, "Company tidak valid.");
        }

        var actor = NormalizeActor(actorUsername);
        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var permissionFailure = await EnsurePermissionAsync(
            connection,
            null,
            actor,
            InventoryModuleCode,
            InventorySubmoduleApiInv,
            PermissionActionSyncUpload,
            companyId,
            null,
            cancellationToken,
            "Anda tidak memiliki izin untuk upload inventory ke server pusat.");
        if (permissionFailure is not null)
        {
            return permissionFailure;
        }

        var masterCompanyId = await GetInventoryMasterCompanyIdInternalAsync(connection, null, cancellationToken);
        if (!masterCompanyId.HasValue || masterCompanyId.Value <= 0)
        {
            return new AccessOperationResult(false, "Master company inventory belum dikonfigurasi.");
        }

        if (masterCompanyId.Value != companyId)
        {
            return new AccessOperationResult(false, "Hanya master company yang diizinkan upload ke server pusat.");
        }

        var settings = await GetInventoryCentralSyncSettingsInternalAsync(connection, null, cancellationToken);
        var settingsError = ValidateCentralSyncSettings(settings);
        if (!string.IsNullOrWhiteSpace(settingsError))
        {
            return new AccessOperationResult(false, settingsError);
        }

        var watermarkFromUtc = await GetInventorySyncWatermarkAsync(connection, null, companyId, InventorySyncDirectionUpload, cancellationToken);
        var changedCategories = await LoadChangedCategoriesForUploadAsync(connection, companyId, watermarkFromUtc, cancellationToken);

        var runId = await CreateInventorySyncRunAsync(
            connection,
            null,
            companyId,
            InventorySyncDirectionUpload,
            InventorySyncTriggerManual,
            actor,
            watermarkFromUtc,
            message: "Upload berjalan.",
            cancellationToken);

        if (changedCategories.Count == 0)
        {
            var nowUtc = DateTime.UtcNow;
            await using var tx = await connection.BeginTransactionAsync(cancellationToken);
            await UpsertInventorySyncWatermarkAsync(connection, tx, companyId, InventorySyncDirectionUpload, nowUtc, cancellationToken);
            await UpdateInventorySyncRunAsync(
                connection,
                tx,
                runId,
                InventorySyncStatusSuccess,
                nowUtc,
                0,
                0,
                0,
                "Tidak ada perubahan kategori untuk di-upload.",
                cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return new AccessOperationResult(true, "Tidak ada perubahan kategori untuk di-upload.", runId);
        }

        var uploadRequest = new CentralUploadRequest
        {
            CompanyId = companyId,
            SinceUtc = watermarkFromUtc,
            GeneratedAtUtc = DateTime.UtcNow,
            Categories = changedCategories
                .Select(x => new CentralCategoryPayload
                {
                    CategoryCode = x.Code,
                    CategoryName = x.Name,
                    AccountCode = x.AccountCode,
                    IsActive = x.IsActive,
                    UpdatedAtUtc = x.UpdatedAtUtc
                })
                .ToList(),
            Items = []
        };

        var uploadResult = await SendUploadRequestAsync(settings, uploadRequest, cancellationToken);

        var perCategoryResults = changedCategories
            .Select(x => new UploadItemLog("-", x.Code, "UPSERT_CATEGORY", uploadResult.IsSuccess ? "SUCCESS" : "FAILED", uploadResult.IsSuccess ? string.Empty : uploadResult.Message))
            .ToList();
        var successCount = perCategoryResults.Count(x => x.Result.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase));
        var failedCount = perCategoryResults.Count - successCount;
        var totalItems = perCategoryResults.Count;
        var status = !uploadResult.IsSuccess
            ? InventorySyncStatusFailed
            : failedCount > 0
                ? InventorySyncStatusPartial
                : InventorySyncStatusSuccess;
        var watermarkToUtc = uploadResult.ServerWatermarkUtc
            ?? GetLatestChangedAt(changedCategories, [])
            ?? DateTime.UtcNow;
        var runMessage = uploadResult.Message;

        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            foreach (var categoryLog in perCategoryResults)
            {
                await InsertInventorySyncItemLogAsync(
                    connection,
                    transaction,
                    runId,
                    companyId,
                    InventorySyncDirectionUpload,
                    categoryLog.ItemCode,
                    categoryLog.CategoryCode,
                    categoryLog.Operation,
                    categoryLog.Result,
                    categoryLog.ErrorMessage,
                    cancellationToken);
            }

            if (status == InventorySyncStatusSuccess || status == InventorySyncStatusPartial)
            {
                await UpsertInventorySyncWatermarkAsync(connection, transaction, companyId, InventorySyncDirectionUpload, watermarkToUtc, cancellationToken);
            }

            await UpdateInventorySyncRunAsync(
                connection,
                transaction,
                runId,
                status,
                watermarkToUtc,
                totalItems,
                successCount,
                failedCount,
                runMessage,
                cancellationToken);

            await InsertAuditLogAsync(
                connection,
                transaction,
                "INV_SYNC",
                runId,
                "UPLOAD_TO_CENTRAL",
                actor,
                $"company_id={companyId};status={status};total_items={totalItems};success={successCount};failed={failedCount}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }

        return new AccessOperationResult(
            status != InventorySyncStatusFailed,
            string.IsNullOrWhiteSpace(runMessage)
                ? $"Upload selesai. Berhasil: {successCount}, gagal: {failedCount}."
                : runMessage,
            runId);
    }

    public async Task<AccessOperationResult> DownloadInventoryFromCentralAsync(
        long companyId,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        if (companyId <= 0)
        {
            return new AccessOperationResult(false, "Company tidak valid.");
        }

        var actor = NormalizeActor(actorUsername);
        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var permissionFailure = await EnsurePermissionAsync(
            connection,
            null,
            actor,
            InventoryModuleCode,
            InventorySubmoduleApiInv,
            PermissionActionSyncDownload,
            companyId,
            null,
            cancellationToken,
            "Anda tidak memiliki izin untuk download inventory dari server pusat.");
        if (permissionFailure is not null)
        {
            return permissionFailure;
        }

        var masterCompanyId = await GetInventoryMasterCompanyIdInternalAsync(connection, null, cancellationToken);
        if (!masterCompanyId.HasValue || masterCompanyId.Value <= 0)
        {
            return new AccessOperationResult(false, "Master company inventory belum dikonfigurasi.");
        }

        if (masterCompanyId.Value == companyId)
        {
            return new AccessOperationResult(false, "Master company tidak perlu download dari pusat.");
        }

        var settings = await GetInventoryCentralSyncSettingsInternalAsync(connection, null, cancellationToken);
        var settingsError = ValidateCentralSyncSettings(settings);
        if (!string.IsNullOrWhiteSpace(settingsError))
        {
            return new AccessOperationResult(false, settingsError);
        }

        var watermarkFromUtc = await GetInventorySyncWatermarkAsync(connection, null, companyId, InventorySyncDirectionDownload, cancellationToken);
        var runId = await CreateInventorySyncRunAsync(
            connection,
            null,
            companyId,
            InventorySyncDirectionDownload,
            InventorySyncTriggerManual,
            actor,
            watermarkFromUtc,
            message: "Download berjalan.",
            cancellationToken);

        var downloadResponse = await SendDownloadRequestAsync(settings, companyId, watermarkFromUtc, cancellationToken);
        var categories = downloadResponse.Categories ?? [];

        var totalItems = categories.Count;
        var successCount = 0;
        var failedCount = 0;
        var status = downloadResponse.IsSuccess ? InventorySyncStatusSuccess : InventorySyncStatusFailed;
        var watermarkToUtc = downloadResponse.ServerWatermarkUtc ?? DateTime.UtcNow;
        var runMessage = downloadResponse.Message;

        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            if (downloadResponse.IsSuccess)
            {
                try
                {
                    _ = await ApplyDownloadedCategoriesAsync(
                        connection,
                        transaction,
                        companyId,
                        categories,
                        actor,
                        cancellationToken);

                    foreach (var category in categories)
                    {
                        await InsertInventorySyncItemLogAsync(
                            connection,
                            transaction,
                            runId,
                            companyId,
                            InventorySyncDirectionDownload,
                            "-",
                            category.CategoryCode,
                            "UPSERT_CATEGORY",
                            "SUCCESS",
                            string.Empty,
                            cancellationToken);
                    }

                    successCount = categories.Count;
                }
                catch (Exception ex)
                {
                    failedCount = categories.Count;
                    status = categories.Count == 0 ? InventorySyncStatusFailed : InventorySyncStatusPartial;
                    runMessage = string.IsNullOrWhiteSpace(runMessage)
                        ? ex.Message
                        : $"{runMessage} {ex.Message}";

                    foreach (var category in categories)
                    {
                        await InsertInventorySyncItemLogAsync(
                            connection,
                            transaction,
                            runId,
                            companyId,
                            InventorySyncDirectionDownload,
                            "-",
                            category.CategoryCode,
                            "UPSERT_CATEGORY",
                            "FAILED",
                            ex.Message,
                            cancellationToken);
                    }
                }

                if (failedCount == 0)
                {
                    await UpsertInventorySyncWatermarkAsync(connection, transaction, companyId, InventorySyncDirectionDownload, watermarkToUtc, cancellationToken);
                }
            }

            if (!downloadResponse.IsSuccess)
            {
                failedCount = totalItems;
            }

            await UpdateInventorySyncRunAsync(
                connection,
                transaction,
                runId,
                status,
                watermarkToUtc,
                totalItems,
                successCount,
                failedCount,
                runMessage,
                cancellationToken);

            await InsertAuditLogAsync(
                connection,
                transaction,
                "INV_SYNC",
                runId,
                "DOWNLOAD_FROM_CENTRAL",
                actor,
                $"company_id={companyId};status={status};total_items={totalItems};success={successCount};failed={failedCount}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }

        return new AccessOperationResult(
            status != InventorySyncStatusFailed,
            string.IsNullOrWhiteSpace(runMessage)
                ? $"Download selesai. Berhasil: {successCount}, gagal: {failedCount}."
                : runMessage,
            runId);
    }

    public async Task<List<ManagedInventorySyncRun>> GetInventorySyncRunHistoryAsync(
        long companyId,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        if (companyId <= 0)
        {
            return [];
        }

        var safeLimit = limit <= 0 ? 100 : Math.Min(limit, 500);
        var output = new List<ManagedInventorySyncRun>();

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(@"
SELECT id,
       company_id,
       direction,
       trigger_mode,
       status,
       actor_username,
       started_at,
       ended_at,
       watermark_from_utc,
       watermark_to_utc,
       total_items,
       success_items,
       failed_items,
       message
FROM inv_sync_runs
WHERE company_id = @company_id
ORDER BY started_at DESC
LIMIT @limit;", connection);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("limit", safeLimit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(new ManagedInventorySyncRun
            {
                Id = reader.GetInt64(0),
                CompanyId = reader.GetInt64(1),
                Direction = reader.GetString(2),
                TriggerMode = reader.GetString(3),
                Status = reader.GetString(4),
                ActorUsername = reader.GetString(5),
                StartedAt = reader.GetDateTime(6),
                EndedAt = reader.IsDBNull(7) ? null : reader.GetDateTime(7),
                WatermarkFromUtc = reader.IsDBNull(8) ? null : reader.GetDateTime(8),
                WatermarkToUtc = reader.IsDBNull(9) ? null : reader.GetDateTime(9),
                TotalItems = reader.IsDBNull(10) ? 0 : reader.GetInt32(10),
                SuccessItems = reader.IsDBNull(11) ? 0 : reader.GetInt32(11),
                FailedItems = reader.IsDBNull(12) ? 0 : reader.GetInt32(12),
                Message = reader.IsDBNull(13) ? string.Empty : reader.GetString(13)
            });
        }

        return output;
    }

    public async Task<List<ManagedInventorySyncItemLog>> GetInventorySyncItemLogHistoryAsync(
        long companyId,
        int limit = 500,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        if (companyId <= 0)
        {
            return [];
        }

        var safeLimit = limit <= 0 ? 500 : Math.Min(limit, 2000);
        var output = new List<ManagedInventorySyncItemLog>();

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(@"
SELECT l.id,
       l.sync_run_id,
       r.company_id,
       l.direction,
       l.item_code,
       l.category_code,
       l.operation,
       l.result,
       l.error_message,
       l.logged_at
FROM inv_sync_item_logs l
JOIN inv_sync_runs r ON r.id = l.sync_run_id
WHERE r.company_id = @company_id
ORDER BY l.logged_at DESC
LIMIT @limit;", connection);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("limit", safeLimit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(new ManagedInventorySyncItemLog
            {
                Id = reader.GetInt64(0),
                SyncRunId = reader.GetInt64(1),
                CompanyId = reader.GetInt64(2),
                Direction = reader.GetString(3),
                ItemCode = reader.GetString(4),
                CategoryCode = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                Operation = reader.GetString(6),
                Result = reader.GetString(7),
                ErrorMessage = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                LoggedAt = reader.GetDateTime(9)
            });
        }

        return output;
    }

    private async Task<InventoryCentralSyncSettings> GetInventoryCentralSyncSettingsInternalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var defaults = LoadCentralSyncSettingsDefaults();

        var baseUrl = await GetSystemSettingValueAsync(connection, transaction, InventoryCentralBaseUrlSettingKey, cancellationToken);
        var apiKey = await GetSystemSettingValueAsync(connection, transaction, InventoryCentralApiKeySettingKey, cancellationToken);
        var uploadPath = await GetSystemSettingValueAsync(connection, transaction, InventoryCentralUploadPathSettingKey, cancellationToken);
        var downloadPath = await GetSystemSettingValueAsync(connection, transaction, InventoryCentralDownloadPathSettingKey, cancellationToken);
        var timeoutRaw = await GetSystemSettingValueAsync(connection, transaction, InventoryCentralTimeoutSettingKey, cancellationToken);

        var timeout = defaults.TimeoutSeconds;
        if (int.TryParse(timeoutRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedTimeout) && parsedTimeout > 0)
        {
            timeout = parsedTimeout;
        }

        return new InventoryCentralSyncSettings
        {
            BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? defaults.BaseUrl : baseUrl.Trim(),
            ApiKey = string.IsNullOrWhiteSpace(apiKey) ? defaults.ApiKey : apiKey.Trim(),
            UploadPath = NormalizeCentralPath(uploadPath, defaults.UploadPath),
            DownloadPath = NormalizeCentralPath(downloadPath, defaults.DownloadPath),
            TimeoutSeconds = timeout
        };
    }

    private static InventoryCentralSyncSettings LoadCentralSyncSettingsDefaults()
    {
        var output = new InventoryCentralSyncSettings();
        try
        {
            var filePath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(filePath))
            {
                return output;
            }

            using var stream = File.OpenRead(filePath);
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("CentralSync", out var section))
            {
                return output;
            }

            if (section.TryGetProperty("BaseUrl", out var baseUrl))
            {
                output.BaseUrl = baseUrl.GetString() ?? string.Empty;
            }

            if (section.TryGetProperty("ApiKey", out var apiKey))
            {
                output.ApiKey = apiKey.GetString() ?? string.Empty;
            }

            if (section.TryGetProperty("UploadPath", out var uploadPath))
            {
                output.UploadPath = NormalizeCentralPath(uploadPath.GetString(), output.UploadPath);
            }

            if (section.TryGetProperty("DownloadPath", out var downloadPath))
            {
                output.DownloadPath = NormalizeCentralPath(downloadPath.GetString(), output.DownloadPath);
            }

            if (section.TryGetProperty("TimeoutSeconds", out var timeoutValue))
            {
                if (timeoutValue.ValueKind == JsonValueKind.Number && timeoutValue.TryGetInt32(out var timeoutFromNumber) && timeoutFromNumber > 0)
                {
                    output.TimeoutSeconds = timeoutFromNumber;
                }
                else if (timeoutValue.ValueKind == JsonValueKind.String &&
                         int.TryParse(timeoutValue.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeoutFromString) &&
                         timeoutFromString > 0)
                {
                    output.TimeoutSeconds = timeoutFromString;
                }
            }
        }
        catch
        {
            // use safe defaults
        }

        return output;
    }

    private static string ValidateCentralSyncSettings(InventoryCentralSyncSettings settings)
    {
        if (settings is null)
        {
            return "Pengaturan sync pusat tidak ditemukan.";
        }

        if (string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            return "Base URL server pusat belum dikonfigurasi.";
        }

        if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out _))
        {
            return "Base URL server pusat tidak valid.";
        }

        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            return "API key server pusat belum dikonfigurasi.";
        }

        return string.Empty;
    }

    private static string NormalizeCentralPath(string? rawPath, string fallbackPath)
    {
        var value = string.IsNullOrWhiteSpace(rawPath) ? fallbackPath : rawPath.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            value = fallbackPath;
        }

        // Untuk mode Oracle compare, field path dapat menyimpan SQL SELECT langsung.
        if (value.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        return value.StartsWith('/') ? value : $"/{value}";
    }

    private static bool IsValidBaseUrlOrConnectionString(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out _))
        {
            return true;
        }

        // Oracle connection string umumnya menggunakan format key=value;key=value.
        return value.Contains('=') && value.Contains(';');
    }

    private static Uri BuildCentralUri(string baseUrl, string path, IReadOnlyDictionary<string, string?>? query = null)
    {
        var baseUri = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        var target = new Uri(baseUri, path.TrimStart('/'));
        if (query is null || query.Count == 0)
        {
            return target;
        }

        var builder = new StringBuilder();
        foreach (var pair in query)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('&');
            }

            builder.Append(Uri.EscapeDataString(pair.Key));
            builder.Append('=');
            builder.Append(Uri.EscapeDataString(pair.Value ?? string.Empty));
        }

        var uriBuilder = new UriBuilder(target)
        {
            Query = builder.ToString()
        };
        return uriBuilder.Uri;
    }

    private static HttpClient BuildCentralHttpClient(InventoryCentralSyncSettings settings)
    {
        var timeoutSeconds = settings.TimeoutSeconds > 0 ? settings.TimeoutSeconds : 30;
        return new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(timeoutSeconds)
        };
    }

    private async Task<UploadCallResult> SendUploadRequestAsync(
        InventoryCentralSyncSettings settings,
        CentralUploadRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            using var httpClient = BuildCentralHttpClient(settings);
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, BuildCentralUri(settings.BaseUrl, settings.UploadPath));
            httpRequest.Headers.TryAddWithoutValidation("X-Api-Key", settings.ApiKey);

            var payloadJson = JsonSerializer.Serialize(request, CentralSyncJsonOptions);
            httpRequest.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

            using var response = await httpClient.SendAsync(httpRequest, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new UploadCallResult(false, $"Upload gagal ({(int)response.StatusCode}): {responseBody}", null, []);
            }

            if (string.IsNullOrWhiteSpace(responseBody))
            {
                return new UploadCallResult(true, "Upload berhasil.", null, []);
            }

            var parsed = JsonSerializer.Deserialize<CentralUploadResponse>(responseBody, CentralSyncJsonOptions) ?? new CentralUploadResponse();
            var responseSuccess = parsed.IsSuccess ?? true;
            var message = string.IsNullOrWhiteSpace(parsed.Message)
                ? (responseSuccess ? "Upload berhasil." : "Upload ditolak server pusat.")
                : parsed.Message!;

            return new UploadCallResult(
                responseSuccess,
                message,
                parsed.ServerWatermarkUtc,
                parsed.ItemResults ?? []);
        }
        catch (Exception ex)
        {
            return new UploadCallResult(false, $"Upload gagal: {ex.Message}", null, []);
        }
    }

    private async Task<DownloadCallResult> SendDownloadRequestAsync(
        InventoryCentralSyncSettings settings,
        long companyId,
        DateTime? watermarkFromUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            using var httpClient = BuildCentralHttpClient(settings);
            var query = new Dictionary<string, string?>
            {
                ["companyId"] = companyId.ToString(CultureInfo.InvariantCulture),
                ["sinceUtc"] = watermarkFromUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            };

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                BuildCentralUri(settings.BaseUrl, settings.DownloadPath, query));
            request.Headers.TryAddWithoutValidation("X-Api-Key", settings.ApiKey);

            using var response = await httpClient.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new DownloadCallResult(false, $"Download gagal ({(int)response.StatusCode}): {responseBody}", null, [], []);
            }

            if (string.IsNullOrWhiteSpace(responseBody))
            {
                return new DownloadCallResult(true, "Download berhasil (tanpa data).", null, [], []);
            }

            var parsed = JsonSerializer.Deserialize<CentralDownloadResponse>(responseBody, CentralSyncJsonOptions);
            if (parsed is null)
            {
                return new DownloadCallResult(false, "Response download server pusat tidak valid.", null, [], []);
            }

            var responseSuccess = parsed.IsSuccess ?? true;
            var message = string.IsNullOrWhiteSpace(parsed.Message)
                ? (responseSuccess ? "Download berhasil." : "Download ditolak server pusat.")
                : parsed.Message!;

            return new DownloadCallResult(
                responseSuccess,
                message,
                parsed.ServerWatermarkUtc,
                parsed.Categories ?? [],
                parsed.Items ?? []);
        }
        catch (Exception ex)
        {
            return new DownloadCallResult(false, $"Download gagal: {ex.Message}", null, [], []);
        }
    }

    private static List<UploadItemLog> MapUploadItemResults(
        IReadOnlyCollection<UploadItemSnapshot> changedItems,
        IReadOnlyCollection<CentralUploadItemResult> responseItemResults)
    {
        var responseMap = responseItemResults
            .Where(x => !string.IsNullOrWhiteSpace(x.ItemCode))
            .GroupBy(x => x.ItemCode!.Trim().ToUpperInvariant(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var output = new List<UploadItemLog>(changedItems.Count);
        foreach (var item in changedItems)
        {
            var code = item.Code.Trim().ToUpperInvariant();
            if (responseMap.TryGetValue(code, out var responseItem))
            {
                var isSuccess = responseItem.IsSuccess
                    ?? string.Equals(responseItem.Result, "SUCCESS", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(responseItem.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase);
                output.Add(new UploadItemLog(
                    item.Code,
                    item.CategoryCode,
                    string.IsNullOrWhiteSpace(responseItem.Operation) ? "UPLOAD" : responseItem.Operation!,
                    isSuccess ? "SUCCESS" : "FAILED",
                    isSuccess ? string.Empty : responseItem.ErrorMessage ?? responseItem.Message ?? "Item gagal di-upload."));
                continue;
            }

            output.Add(new UploadItemLog(item.Code, item.CategoryCode, "UPLOAD", "SUCCESS", string.Empty));
        }

        return output;
    }

    private static DateTime? GetLatestChangedAt(
        IReadOnlyCollection<UploadCategorySnapshot> categories,
        IReadOnlyCollection<UploadItemSnapshot> items)
    {
        DateTime? latest = null;
        foreach (var category in categories)
        {
            latest = !latest.HasValue || category.UpdatedAtUtc > latest.Value
                ? category.UpdatedAtUtc
                : latest;
        }

        foreach (var item in items)
        {
            latest = !latest.HasValue || item.UpdatedAtUtc > latest.Value
                ? item.UpdatedAtUtc
                : latest;
        }

        return latest;
    }

    private async Task<List<UploadCategorySnapshot>> LoadChangedCategoriesForUploadAsync(
        NpgsqlConnection connection,
        long companyId,
        DateTime? watermarkFromUtc,
        CancellationToken cancellationToken)
    {
        var output = new List<UploadCategorySnapshot>();
        await using var command = new NpgsqlCommand(@"
SELECT category_code,
       category_name,
       account_code,
       is_active,
       COALESCE(updated_at, created_at, NOW()) AS changed_at
FROM inv_categories
WHERE @watermark_utc IS NULL OR COALESCE(updated_at, created_at, NOW()) > @watermark_utc
ORDER BY category_code;", connection);
        command.Parameters.Add(new NpgsqlParameter("watermark_utc", NpgsqlDbType.TimestampTz)
        {
            Value = watermarkFromUtc.HasValue ? watermarkFromUtc.Value : DBNull.Value
        });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(new UploadCategorySnapshot(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                !reader.IsDBNull(3) && reader.GetBoolean(3),
                reader.GetDateTime(4).ToUniversalTime()));
        }

        return output;
    }

    private async Task<List<UploadItemSnapshot>> LoadChangedItemsForUploadAsync(
        NpgsqlConnection connection,
        long companyId,
        DateTime? watermarkFromUtc,
        CancellationToken cancellationToken)
    {
        var output = new List<UploadItemSnapshot>();
        await using var command = new NpgsqlCommand(@"
SELECT i.item_code,
       i.item_name,
       i.uom,
       COALESCE(c.category_code, ''),
       i.is_active,
       COALESCE(i.updated_at, i.created_at, NOW()) AS changed_at
FROM inv_items i
LEFT JOIN inv_categories c ON c.id = i.category_id
WHERE @watermark_utc IS NULL OR COALESCE(i.updated_at, i.created_at, NOW()) > @watermark_utc
ORDER BY i.item_code;", connection);
        command.Parameters.Add(new NpgsqlParameter("watermark_utc", NpgsqlDbType.TimestampTz)
        {
            Value = watermarkFromUtc.HasValue ? watermarkFromUtc.Value : DBNull.Value
        });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(new UploadItemSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                !reader.IsDBNull(4) && reader.GetBoolean(4),
                reader.GetDateTime(5).ToUniversalTime()));
        }

        return output;
    }

    private static async Task<long> CreateInventorySyncRunAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        long companyId,
        string direction,
        string triggerMode,
        string actor,
        DateTime? watermarkFromUtc,
        string message,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(@"
INSERT INTO inv_sync_runs (
    company_id,
    direction,
    trigger_mode,
    status,
    actor_username,
    started_at,
    watermark_from_utc,
    message
)
VALUES (
    @company_id,
    @direction,
    @trigger_mode,
    @status,
    @actor_username,
    NOW(),
    @watermark_from_utc,
    @message
)
RETURNING id;", connection, transaction);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("direction", direction);
        command.Parameters.AddWithValue("trigger_mode", triggerMode);
        command.Parameters.AddWithValue("status", InventorySyncStatusRunning);
        command.Parameters.AddWithValue("actor_username", actor);
        command.Parameters.Add(new NpgsqlParameter("watermark_from_utc", NpgsqlDbType.TimestampTz)
        {
            Value = watermarkFromUtc.HasValue ? watermarkFromUtc.Value : DBNull.Value
        });
        command.Parameters.AddWithValue("message", message ?? string.Empty);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task UpdateInventorySyncRunAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long runId,
        string status,
        DateTime? watermarkToUtc,
        int totalItems,
        int successItems,
        int failedItems,
        string message,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(@"
UPDATE inv_sync_runs
SET status = @status,
    ended_at = NOW(),
    watermark_to_utc = @watermark_to_utc,
    total_items = @total_items,
    success_items = @success_items,
    failed_items = @failed_items,
    message = @message
WHERE id = @id;", connection, transaction);
        command.Parameters.AddWithValue("id", runId);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.Add(new NpgsqlParameter("watermark_to_utc", NpgsqlDbType.TimestampTz)
        {
            Value = watermarkToUtc.HasValue ? watermarkToUtc.Value : DBNull.Value
        });
        command.Parameters.AddWithValue("total_items", totalItems);
        command.Parameters.AddWithValue("success_items", successItems);
        command.Parameters.AddWithValue("failed_items", failedItems);
        command.Parameters.AddWithValue("message", message ?? string.Empty);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertInventorySyncItemLogAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long runId,
        long companyId,
        string direction,
        string itemCode,
        string categoryCode,
        string operation,
        string result,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(@"
INSERT INTO inv_sync_item_logs (
    sync_run_id,
    company_id,
    direction,
    item_code,
    category_code,
    operation,
    result,
    error_message,
    logged_at
)
VALUES (
    @sync_run_id,
    @company_id,
    @direction,
    @item_code,
    @category_code,
    @operation,
    @result,
    @error_message,
    NOW()
);", connection, transaction);
        command.Parameters.AddWithValue("sync_run_id", runId);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("direction", direction);
        command.Parameters.AddWithValue("item_code", string.IsNullOrWhiteSpace(itemCode) ? "-" : itemCode.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("category_code", string.IsNullOrWhiteSpace(categoryCode) ? string.Empty : categoryCode.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("operation", string.IsNullOrWhiteSpace(operation) ? "UPSERT" : operation.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("result", string.IsNullOrWhiteSpace(result) ? "SUCCESS" : result.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("error_message", errorMessage ?? string.Empty);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<DateTime?> GetInventorySyncWatermarkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        long companyId,
        string direction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(@"
SELECT last_success_utc
FROM inv_sync_watermarks
WHERE company_id = @company_id
  AND direction = @direction;", connection, transaction);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("direction", direction);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        if (scalar is null || scalar is DBNull)
        {
            return null;
        }

        return Convert.ToDateTime(scalar, CultureInfo.InvariantCulture).ToUniversalTime();
    }

    private static async Task UpsertInventorySyncWatermarkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        string direction,
        DateTime watermarkUtc,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(@"
INSERT INTO inv_sync_watermarks (company_id, direction, last_success_utc, updated_at)
VALUES (@company_id, @direction, @last_success_utc, NOW())
ON CONFLICT (company_id, direction) DO UPDATE
SET last_success_utc = EXCLUDED.last_success_utc,
    updated_at = NOW();", connection, transaction);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("direction", direction);
        command.Parameters.AddWithValue("last_success_utc", watermarkUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string?> GetSystemSettingValueAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string settingKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(@"
SELECT setting_value
FROM app_system_settings
WHERE setting_key = @setting_key
LIMIT 1;", connection, transaction);
        command.Parameters.AddWithValue("setting_key", settingKey);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        if (scalar is null || scalar is DBNull)
        {
            return null;
        }

        return Convert.ToString(scalar, CultureInfo.InvariantCulture);
    }

    private static async Task UpsertSystemSettingValueAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string settingKey,
        string settingValue,
        string actor,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(@"
INSERT INTO app_system_settings (setting_key, setting_value, updated_by, updated_at)
VALUES (@setting_key, @setting_value, @updated_by, NOW())
ON CONFLICT (setting_key) DO UPDATE
SET setting_value = EXCLUDED.setting_value,
    updated_by = EXCLUDED.updated_by,
    updated_at = NOW();", connection, transaction);
        command.Parameters.AddWithValue("setting_key", settingKey);
        command.Parameters.AddWithValue("setting_value", settingValue ?? string.Empty);
        command.Parameters.AddWithValue("updated_by", actor);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Dictionary<string, DownloadCategoryMapEntry>> ApplyDownloadedCategoriesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        IReadOnlyCollection<CentralCategoryPayload> categories,
        string actor,
        CancellationToken cancellationToken)
    {
        foreach (var category in categories)
        {
            if (string.IsNullOrWhiteSpace(category.CategoryCode))
            {
                continue;
            }

            var code = category.CategoryCode.Trim().ToUpperInvariant();
            var name = string.IsNullOrWhiteSpace(category.CategoryName) ? code : category.CategoryName.Trim();
            var accountCode = (category.AccountCode ?? string.Empty).Trim().ToUpperInvariant();

            long? existingId = null;
            await using (var checkCommand = new NpgsqlCommand(@"
SELECT id
FROM inv_categories
WHERE upper(category_code) = @category_code
FOR UPDATE;", connection, transaction))
            {
                checkCommand.Parameters.AddWithValue("category_code", code);
                var scalar = await checkCommand.ExecuteScalarAsync(cancellationToken);
                if (scalar is not null && scalar is not DBNull)
                {
                    existingId = Convert.ToInt64(scalar);
                }
            }

            if (existingId.HasValue)
            {
                await using var updateCommand = new NpgsqlCommand(@"
UPDATE inv_categories
SET category_name = @category_name,
    account_code = @account_code,
    is_active = @is_active,
    updated_by = @updated_by,
    updated_at = NOW()
WHERE id = @id;", connection, transaction);
                updateCommand.Parameters.AddWithValue("id", existingId.Value);
                updateCommand.Parameters.AddWithValue("category_name", name);
                updateCommand.Parameters.AddWithValue("account_code", accountCode);
                updateCommand.Parameters.AddWithValue("is_active", category.IsActive);
                updateCommand.Parameters.AddWithValue("updated_by", actor);
                await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            }
            else
            {
                await using var insertCommand = new NpgsqlCommand(@"
INSERT INTO inv_categories (
    category_code,
    category_name,
    account_code,
    is_active,
    created_by,
    created_at,
    updated_at
)
VALUES (
    @category_code,
    @category_name,
    @account_code,
    @is_active,
    @created_by,
    NOW(),
    NOW()
);", connection, transaction);
                insertCommand.Parameters.AddWithValue("category_code", code);
                insertCommand.Parameters.AddWithValue("category_name", name);
                insertCommand.Parameters.AddWithValue("account_code", accountCode);
                insertCommand.Parameters.AddWithValue("is_active", category.IsActive);
                insertCommand.Parameters.AddWithValue("created_by", actor);
                await insertCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        var map = new Dictionary<string, DownloadCategoryMapEntry>(StringComparer.OrdinalIgnoreCase);
        await using var mapCommand = new NpgsqlCommand(@"
SELECT id, category_code, category_name
FROM inv_categories;", connection, transaction);
        await using var reader = await mapCommand.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            map[reader.GetString(1)] = new DownloadCategoryMapEntry(
                reader.GetInt64(0),
                reader.GetString(2));
        }

        return map;
    }

    private static async Task UpsertDownloadedItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        CentralItemPayload item,
        IReadOnlyDictionary<string, DownloadCategoryMapEntry> categoryMap,
        string actor,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.ItemCode))
        {
            throw new InvalidOperationException("Item code kosong.");
        }

        var itemCode = item.ItemCode.Trim().ToUpperInvariant();
        var itemName = string.IsNullOrWhiteSpace(item.ItemName) ? itemCode : item.ItemName.Trim();
        var uom = string.IsNullOrWhiteSpace(item.Uom) ? "PCS" : item.Uom.Trim().ToUpperInvariant();
        var categoryCode = (item.CategoryCode ?? string.Empty).Trim().ToUpperInvariant();

        long? categoryId = null;
        var categoryName = string.Empty;
        if (!string.IsNullOrWhiteSpace(categoryCode) && categoryMap.TryGetValue(categoryCode, out var category))
        {
            categoryId = category.Id;
            categoryName = category.Name;
        }

        long? existingId = null;
        await using (var checkCommand = new NpgsqlCommand(@"
SELECT id
FROM inv_items
WHERE upper(item_code) = @item_code
FOR UPDATE;", connection, transaction))
        {
            checkCommand.Parameters.AddWithValue("item_code", itemCode);
            var scalar = await checkCommand.ExecuteScalarAsync(cancellationToken);
            if (scalar is not null && scalar is not DBNull)
            {
                existingId = Convert.ToInt64(scalar);
            }
        }

        object categoryIdParam = categoryId.HasValue ? categoryId.Value : DBNull.Value;
        if (existingId.HasValue)
        {
            await using var updateCommand = new NpgsqlCommand(@"
UPDATE inv_items
SET category_id = @category_id,
    item_name = @item_name,
    uom = @uom,
    category = @category,
    is_active = @is_active,
    updated_by = @updated_by,
    updated_at = NOW()
WHERE id = @id;", connection, transaction);
            updateCommand.Parameters.AddWithValue("id", existingId.Value);
            updateCommand.Parameters.Add(new NpgsqlParameter("category_id", NpgsqlDbType.Bigint) { Value = categoryIdParam });
            updateCommand.Parameters.AddWithValue("item_name", itemName);
            updateCommand.Parameters.AddWithValue("uom", uom);
            updateCommand.Parameters.AddWithValue("category", categoryName);
            updateCommand.Parameters.AddWithValue("is_active", item.IsActive);
            updateCommand.Parameters.AddWithValue("updated_by", actor);
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        await using var insertCommand = new NpgsqlCommand(@"
INSERT INTO inv_items (
    category_id,
    item_code,
    item_name,
    uom,
    category,
    is_active,
    created_by,
    created_at,
    updated_at
)
VALUES (
    @category_id,
    @item_code,
    @item_name,
    @uom,
    @category,
    @is_active,
    @created_by,
    NOW(),
    NOW()
);", connection, transaction);
        insertCommand.Parameters.Add(new NpgsqlParameter("category_id", NpgsqlDbType.Bigint) { Value = categoryIdParam });
        insertCommand.Parameters.AddWithValue("item_code", itemCode);
        insertCommand.Parameters.AddWithValue("item_name", itemName);
        insertCommand.Parameters.AddWithValue("uom", uom);
        insertCommand.Parameters.AddWithValue("category", categoryName);
        insertCommand.Parameters.AddWithValue("is_active", item.IsActive);
        insertCommand.Parameters.AddWithValue("created_by", actor);
        await insertCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private readonly record struct UploadCategorySnapshot(
        string Code,
        string Name,
        string AccountCode,
        bool IsActive,
        DateTime UpdatedAtUtc);

    private readonly record struct UploadItemSnapshot(
        string Code,
        string Name,
        string Uom,
        string CategoryCode,
        bool IsActive,
        DateTime UpdatedAtUtc);

    private readonly record struct UploadItemLog(
        string ItemCode,
        string CategoryCode,
        string Operation,
        string Result,
        string ErrorMessage);

    private readonly record struct UploadCallResult(
        bool IsSuccess,
        string Message,
        DateTime? ServerWatermarkUtc,
        IReadOnlyCollection<CentralUploadItemResult> ItemResults);

    private readonly record struct DownloadCallResult(
        bool IsSuccess,
        string Message,
        DateTime? ServerWatermarkUtc,
        IReadOnlyCollection<CentralCategoryPayload> Categories,
        IReadOnlyCollection<CentralItemPayload> Items);

    private readonly record struct DownloadCategoryMapEntry(
        long Id,
        string Name);

    private sealed class CentralUploadRequest
    {
        public long CompanyId { get; init; }

        public DateTime? SinceUtc { get; init; }

        public DateTime GeneratedAtUtc { get; init; }

        public List<CentralCategoryPayload> Categories { get; init; } = [];

        public List<CentralItemPayload> Items { get; init; } = [];
    }

    private sealed class CentralUploadResponse
    {
        public bool? IsSuccess { get; init; }

        public string? Message { get; init; }

        public DateTime? ServerWatermarkUtc { get; init; }

        public List<CentralUploadItemResult>? ItemResults { get; init; }
    }

    private sealed class CentralDownloadResponse
    {
        public bool? IsSuccess { get; init; }

        public string? Message { get; init; }

        public DateTime? ServerWatermarkUtc { get; init; }

        public List<CentralCategoryPayload>? Categories { get; init; }

        public List<CentralItemPayload>? Items { get; init; }
    }

    private sealed class CentralUploadItemResult
    {
        public string? ItemCode { get; init; }

        public string? Operation { get; init; }

        public string? Result { get; init; }

        public string? Status { get; init; }

        public bool? IsSuccess { get; init; }

        public string? Message { get; init; }

        public string? ErrorMessage { get; init; }
    }

    private sealed class CentralCategoryPayload
    {
        public string CategoryCode { get; init; } = string.Empty;

        public string CategoryName { get; init; } = string.Empty;

        public string AccountCode { get; init; } = string.Empty;

        public bool IsActive { get; init; } = true;

        public DateTime? UpdatedAtUtc { get; init; }
    }

    private sealed class CentralItemPayload
    {
        public string ItemCode { get; init; } = string.Empty;

        public string ItemName { get; init; } = string.Empty;

        public string Uom { get; init; } = "PCS";

        public string CategoryCode { get; init; } = string.Empty;

        public bool IsActive { get; init; } = true;

        public DateTime? UpdatedAtUtc { get; init; }
    }
}


