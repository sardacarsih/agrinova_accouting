namespace Accounting.Services;

public sealed class JournalImportCommitResult
{
    public int SavedCount { get; init; }

    public int FailedCount { get; init; }

    public long FirstSavedId { get; init; }

    public string Message { get; init; } = string.Empty;

    public string StatusMessage { get; init; } = string.Empty;

    public List<string> FailedDetails { get; init; } = new();
}

public sealed class JournalExportResult
{
    public bool IsSuccess { get; init; }

    public int ExportedCount { get; init; }

    public string Message { get; init; } = string.Empty;
}

public enum JournalExportLayout
{
    FlatJournals = 0,
    HeaderDetailLegacy = 1
}

public sealed class JournalImportExportWorkflow
{
    private readonly IAccessControlService _accessControlService;
    private readonly JournalXlsxService _xlsxService;

    public JournalImportExportWorkflow(IAccessControlService accessControlService, JournalXlsxService xlsxService)
    {
        _accessControlService = accessControlService;
        _xlsxService = xlsxService;
    }

    public JournalImportLoadResult PreviewImport(
        string importFilePath,
        IReadOnlyDictionary<string, ManagedAccount> accountLookupByCode,
        IReadOnlyDictionary<string, ManagedCostCenter>? costCenterLookupByCode = null)
    {
        var source = _xlsxService.Import(importFilePath);
        return ApplyCoaValidation(source, accountLookupByCode, costCenterLookupByCode);
    }

    public async Task<JournalImportCommitResult> CommitImportAsync(
        IReadOnlyList<JournalImportBundleResult> bundles,
        long companyId,
        long locationId,
        string actorUsername)
    {
        if (bundles is null || bundles.Count == 0)
        {
            return new JournalImportCommitResult
            {
                SavedCount = 0,
                FailedCount = 0,
                FirstSavedId = 0,
                Message = "Tidak ada jurnal valid untuk diimport. Jalankan preview lalu lihat kolom Pesan untuk detail kegagalan.",
                StatusMessage = "Tidak ada jurnal valid untuk diimport."
            };
        }

        var savedCount = 0;
        var failedCount = 0;
        var firstSavedId = 0L;
        var failedMessages = new List<string>();

        foreach (var bundle in bundles)
        {
            var header = new ManagedJournalHeader
            {
                Id = 0,
                CompanyId = companyId,
                LocationId = locationId,
                JournalNo = bundle.Header.JournalNo,
                JournalDate = bundle.Header.JournalDate,
                PeriodMonth = bundle.Header.PeriodMonth == default
                    ? new DateTime(bundle.Header.JournalDate.Year, bundle.Header.JournalDate.Month, 1)
                    : new DateTime(bundle.Header.PeriodMonth.Year, bundle.Header.PeriodMonth.Month, 1),
                ReferenceNo = bundle.Header.ReferenceNo,
                Description = bundle.Header.Description,
                Status = "DRAFT"
            };

            var result = await _accessControlService.SaveJournalDraftAsync(header, bundle.Lines, actorUsername);
            if (result.IsSuccess)
            {
                savedCount++;
                if (firstSavedId <= 0)
                {
                    firstSavedId = result.EntityId ?? 0;
                }
            }
            else
            {
                failedCount++;
                failedMessages.Add($"{bundle.Header.JournalNo}: {result.Message}");
            }
        }

        var message = BuildCommitResultMessage(savedCount, failedCount, failedMessages);
        var statusMessage = failedMessages.Count > 0
            ? $"Sebagian import gagal. Contoh: {failedMessages[0]}"
            : message;

        return new JournalImportCommitResult
        {
            SavedCount = savedCount,
            FailedCount = failedCount,
            FirstSavedId = firstSavedId,
            Message = message,
            StatusMessage = statusMessage,
            FailedDetails = failedMessages
        };
    }

    private static string BuildCommitResultMessage(int savedCount, int failedCount, IReadOnlyList<string> failedMessages)
    {
        var summary = failedCount > 0
            ? $"Import selesai: {savedCount} jurnal tersimpan, {failedCount} gagal."
            : $"Import selesai: {savedCount} jurnal tersimpan.";

        if (failedMessages.Count == 0)
        {
            return summary;
        }

        const int maxShown = 5;
        var detailLines = failedMessages
            .Take(maxShown)
            .Select((message, index) => $"{index + 1}. {message}")
            .ToList();

        if (failedMessages.Count > maxShown)
        {
            detailLines.Add($"Dan {failedMessages.Count - maxShown} jurnal gagal lainnya.");
        }

        return $"{summary}{Environment.NewLine}Detail gagal:{Environment.NewLine}{string.Join(Environment.NewLine, detailLines)}";
    }

    public void ExportCurrent(string filePath, ManagedJournalHeader header, IReadOnlyCollection<ManagedJournalLine> lines)
    {
        _xlsxService.Export(filePath, header, lines);
    }

    public async Task<JournalExportResult> ExportSelectedAsync(
        IReadOnlyCollection<ManagedJournalSummary> selectedSummaries,
        long companyId,
        long locationId,
        string actorUsername,
        string filePath,
        JournalExportLayout layout = JournalExportLayout.FlatJournals)
    {
        if (selectedSummaries.Count == 0)
        {
            return new JournalExportResult
            {
                IsSuccess = false,
                ExportedCount = 0,
                Message = "Pilih jurnal dari tab Daftar terlebih dahulu."
            };
        }

        var bundles = new List<ManagedJournalBundle>();
        foreach (var summary in selectedSummaries)
        {
            var bundle = await _accessControlService.GetJournalBundleAsync(summary.Id, companyId, locationId, actorUsername);
            if (bundle is not null)
            {
                bundles.Add(bundle);
            }
        }

        if (bundles.Count == 0)
        {
            return new JournalExportResult
            {
                IsSuccess = false,
                ExportedCount = 0,
                Message = "Jurnal terpilih tidak ditemukan."
            };
        }

        if (layout == JournalExportLayout.HeaderDetailLegacy)
        {
            _xlsxService.ExportManyLegacy(filePath, bundles);
        }
        else
        {
            _xlsxService.ExportMany(filePath, bundles);
        }

        var message = layout == JournalExportLayout.HeaderDetailLegacy
            ? $"Export {bundles.Count} jurnal berhasil (format lama: Header+Detail)."
            : $"Export {bundles.Count} jurnal berhasil.";
        return new JournalExportResult
        {
            IsSuccess = true,
            ExportedCount = bundles.Count,
            Message = message
        };
    }

    private static JournalImportLoadResult ApplyCoaValidation(
        JournalImportLoadResult source,
        IReadOnlyDictionary<string, ManagedAccount> accountLookupByCode,
        IReadOnlyDictionary<string, ManagedCostCenter>? costCenterLookupByCode)
    {
        if (!source.PreviewItems.Any())
        {
            return source;
        }

        if (source.JournalBundles.Count == 0)
        {
            return new JournalImportLoadResult
            {
                IsSuccess = false,
                Message = source.Message,
                JournalBundles = new List<JournalImportBundleResult>(),
                PreviewItems = source.PreviewItems
            };
        }

        var rewrittenPreview = new List<JournalImportPreviewItem>(source.PreviewItems.Count);
        foreach (var item in source.PreviewItems)
        {
            var normalizedCode = NormalizeAccountCode(item.AccountCode);
            ManagedAccount? account = null;
            var hasCoaCode = !string.IsNullOrWhiteSpace(normalizedCode) &&
                             accountLookupByCode.TryGetValue(normalizedCode, out account);
            var normalizedCostCenterCode = NormalizeCostCenterCode(item.CostCenterCode);
            ManagedCostCenter? costCenter = null;
            var hasCostCenterCode = !string.IsNullOrWhiteSpace(normalizedCostCenterCode) &&
                                    costCenterLookupByCode is not null &&
                                    costCenterLookupByCode.TryGetValue(normalizedCostCenterCode, out costCenter);
            var requiresCostCenter = account?.RequiresCostCenter == true;
            var isValid = item.IsValid && hasCoaCode;
            var message = item.ValidationMessage;

            if (item.IsValid && !hasCoaCode)
            {
                message = string.IsNullOrWhiteSpace(normalizedCode)
                    ? "AccountCode wajib diisi."
                    : $"Kode akun '{normalizedCode}' tidak ditemukan di COA aktif.";
            }
            else if (item.IsValid && requiresCostCenter && string.IsNullOrWhiteSpace(normalizedCostCenterCode))
            {
                isValid = false;
                message = $"Akun '{normalizedCode}' wajib memakai cost center.";
            }
            else if (item.IsValid && !string.IsNullOrWhiteSpace(normalizedCostCenterCode))
            {
                if (!hasCostCenterCode)
                {
                    if (requiresCostCenter)
                    {
                        isValid = false;
                        message = $"Cost center '{normalizedCostCenterCode}' tidak ditemukan.";
                    }
                }
                else if (costCenter is not null && !costCenter.IsPosting)
                {
                    isValid = false;
                    message = $"Cost center '{normalizedCostCenterCode}' bukan level posting.";
                }
            }

            rewrittenPreview.Add(new JournalImportPreviewItem
            {
                RowNumber = item.RowNumber,
                LineNo = item.LineNo,
                JournalNo = item.JournalNo,
                AccountCode = normalizedCode,
                AccountName = account?.Name ?? string.Empty,
                Description = item.Description,
                Debit = item.Debit,
                Credit = item.Credit,
                DepartmentCode = item.DepartmentCode,
                ProjectCode = item.ProjectCode,
                CostCenterCode = hasCostCenterCode ? costCenter!.CostCenterCode : normalizedCostCenterCode,
                IsValid = isValid,
                ValidationMessage = isValid ? string.Empty : message
            });
        }

        var rewrittenBundles = new List<JournalImportBundleResult>(source.JournalBundles.Count);
        foreach (var bundle in source.JournalBundles)
        {
            var normalizedLines = new List<ManagedJournalLine>(bundle.Lines.Count);
            var invalidCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var invalidCostCenters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in bundle.Lines)
            {
                var normalizedCode = NormalizeAccountCode(line.AccountCode);
                if (!accountLookupByCode.TryGetValue(normalizedCode, out var account))
                {
                    invalidCodes.Add(normalizedCode);
                }

                var normalizedCostCenterCode = NormalizeCostCenterCode(line.CostCenterCode);
                ManagedCostCenter? costCenter = null;
                if (!string.IsNullOrWhiteSpace(normalizedCostCenterCode))
                {
                    if (costCenterLookupByCode is null ||
                        !costCenterLookupByCode.TryGetValue(normalizedCostCenterCode, out costCenter) ||
                        !costCenter.IsPosting)
                    {
                        invalidCostCenters.Add(normalizedCostCenterCode);
                    }
                }
                else if (account?.RequiresCostCenter == true)
                {
                    invalidCostCenters.Add($"REQ:{normalizedCode}");
                }

                normalizedLines.Add(new ManagedJournalLine
                {
                    LineNo = line.LineNo,
                    AccountCode = normalizedCode,
                    AccountName = account?.Name ?? string.Empty,
                    Description = line.Description,
                    Debit = line.Debit,
                    Credit = line.Credit,
                    DepartmentCode = line.DepartmentCode,
                    ProjectCode = line.ProjectCode,
                    CostCenterId = costCenter?.Id,
                    CostCenterCode = costCenter?.CostCenterCode ?? normalizedCostCenterCode
                });
            }

            var isValid = bundle.IsValid;
            var validationMessage = bundle.ValidationMessage;
            if (invalidCodes.Count > 0)
            {
                isValid = false;
                validationMessage = $"Kode akun tidak ditemukan di COA: {string.Join(", ", invalidCodes.Where(x => !string.IsNullOrWhiteSpace(x)).Take(3))}.";
            }

            if (isValid && invalidCostCenters.Count > 0)
            {
                isValid = false;
                var missingRequirement = invalidCostCenters
                    .FirstOrDefault(x => x.StartsWith("REQ:", StringComparison.OrdinalIgnoreCase));
                validationMessage = missingRequirement is not null
                    ? $"Akun '{missingRequirement[4..]}' wajib memakai cost center aktif level posting."
                    : $"Cost center tidak valid: {string.Join(", ", invalidCostCenters.Take(3))}.";
            }

            if (isValid && normalizedLines.Count == 0)
            {
                isValid = false;
                validationMessage = $"Jurnal {bundle.Header.JournalNo} tidak memiliki detail valid.";
            }

            if (isValid)
            {
                var totalDebit = normalizedLines.Sum(x => x.Debit);
                var totalCredit = normalizedLines.Sum(x => x.Credit);
                if (totalDebit != totalCredit)
                {
                    isValid = false;
                    validationMessage = $"Jurnal {bundle.Header.JournalNo} tidak seimbang (debit != kredit).";
                }
            }

            rewrittenBundles.Add(new JournalImportBundleResult
            {
                Header = bundle.Header,
                Lines = normalizedLines
                    .OrderBy(x => x.LineNo)
                    .Select((x, idx) =>
                    {
                        x.LineNo = idx + 1;
                        return x;
                    })
                    .ToList(),
                IsValid = isValid,
                ValidationMessage = validationMessage
            });
        }

        foreach (var invalidBundle in rewrittenBundles.Where(x => !x.IsValid))
        {
            for (var index = 0; index < rewrittenPreview.Count; index++)
            {
                var current = rewrittenPreview[index];
                if (!string.Equals(current.JournalNo, invalidBundle.Header.JournalNo, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!current.IsValid)
                {
                    continue;
                }

                rewrittenPreview[index] = new JournalImportPreviewItem
                {
                    RowNumber = current.RowNumber,
                    LineNo = current.LineNo,
                    JournalNo = current.JournalNo,
                    AccountCode = current.AccountCode,
                    AccountName = current.AccountName,
                    Description = current.Description,
                    Debit = current.Debit,
                    Credit = current.Credit,
                    DepartmentCode = current.DepartmentCode,
                    ProjectCode = current.ProjectCode,
                    CostCenterCode = current.CostCenterCode,
                    IsValid = false,
                    ValidationMessage = invalidBundle.ValidationMessage
                };
            }
        }

        var validBundles = rewrittenBundles.Where(x => x.IsValid).ToList();
        var invalidBundleCount = rewrittenBundles.Count - validBundles.Count;
        return new JournalImportLoadResult
        {
            IsSuccess = validBundles.Count > 0,
            Message = validBundles.Count == 0
                ? "Tidak ada jurnal valid untuk diimport setelah validasi COA."
                : invalidBundleCount > 0
                    ? $"Preview selesai: {validBundles.Count} jurnal valid, {invalidBundleCount} jurnal invalid."
                    : $"Preview selesai: {validBundles.Count} jurnal valid.",
            JournalBundles = rewrittenBundles,
            PreviewItems = rewrittenPreview
        };
    }

    private static string NormalizeAccountCode(string? code)
    {
        return string.IsNullOrWhiteSpace(code)
            ? string.Empty
            : code.Trim().ToUpperInvariant();
    }

    private static string NormalizeCostCenterCode(string? code)
    {
        return string.IsNullOrWhiteSpace(code)
            ? string.Empty
            : code.Trim().ToUpperInvariant();
    }
}
