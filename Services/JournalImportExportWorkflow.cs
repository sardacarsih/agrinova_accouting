namespace Accounting.Services;

public sealed class JournalImportCommitResult
{
    public int SavedCount { get; init; }

    public int FailedCount { get; init; }

    public long FirstSavedId { get; init; }

    public string Message { get; init; } = string.Empty;

    public string StatusMessage { get; init; } = string.Empty;
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

    public JournalImportLoadResult PreviewImport(string importFilePath, IReadOnlyDictionary<string, ManagedAccount> accountLookupByCode)
    {
        var source = _xlsxService.Import(importFilePath);
        return ApplyCoaValidation(source, accountLookupByCode);
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
                Message = "Tidak ada jurnal valid untuk diimport.",
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

        var message = failedCount > 0
            ? $"Import selesai: {savedCount} jurnal tersimpan, {failedCount} gagal."
            : $"Import selesai: {savedCount} jurnal tersimpan.";
        var statusMessage = failedMessages.Count > 0
            ? $"Sebagian import gagal. Contoh: {failedMessages[0]}"
            : message;

        return new JournalImportCommitResult
        {
            SavedCount = savedCount,
            FailedCount = failedCount,
            FirstSavedId = firstSavedId,
            Message = message,
            StatusMessage = statusMessage
        };
    }

    public void ExportCurrent(string filePath, ManagedJournalHeader header, IReadOnlyCollection<ManagedJournalLine> lines)
    {
        _xlsxService.Export(filePath, header, lines);
    }

    public async Task<JournalExportResult> ExportSelectedAsync(
        IReadOnlyCollection<ManagedJournalSummary> selectedSummaries,
        long companyId,
        long locationId,
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
            var bundle = await _accessControlService.GetJournalBundleAsync(summary.Id, companyId, locationId);
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
        IReadOnlyDictionary<string, ManagedAccount> accountLookupByCode)
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
            var hasCoaCode = !string.IsNullOrWhiteSpace(normalizedCode) &&
                             accountLookupByCode.ContainsKey(normalizedCode);
            var isValid = item.IsValid && hasCoaCode;
            var message = item.ValidationMessage;

            if (item.IsValid && !hasCoaCode)
            {
                message = string.IsNullOrWhiteSpace(normalizedCode)
                    ? "AccountCode wajib diisi."
                    : $"Kode akun '{normalizedCode}' tidak ditemukan di COA aktif.";
            }

            rewrittenPreview.Add(new JournalImportPreviewItem
            {
                RowNumber = item.RowNumber,
                JournalNo = item.JournalNo,
                AccountCode = normalizedCode,
                Description = item.Description,
                Debit = item.Debit,
                Credit = item.Credit,
                DepartmentCode = item.DepartmentCode,
                ProjectCode = item.ProjectCode,
                CostCenterCode = item.CostCenterCode,
                IsValid = isValid,
                ValidationMessage = isValid ? string.Empty : message
            });
        }

        var rewrittenBundles = new List<JournalImportBundleResult>(source.JournalBundles.Count);
        foreach (var bundle in source.JournalBundles)
        {
            var normalizedLines = new List<ManagedJournalLine>(bundle.Lines.Count);
            var invalidCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in bundle.Lines)
            {
                var normalizedCode = NormalizeAccountCode(line.AccountCode);
                if (!accountLookupByCode.TryGetValue(normalizedCode, out var account))
                {
                    invalidCodes.Add(normalizedCode);
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
                    CostCenterCode = line.CostCenterCode
                });
            }

            var isValid = bundle.IsValid;
            var validationMessage = bundle.ValidationMessage;
            if (invalidCodes.Count > 0)
            {
                isValid = false;
                validationMessage = $"Kode akun tidak ditemukan di COA: {string.Join(", ", invalidCodes.Where(x => !string.IsNullOrWhiteSpace(x)).Take(3))}.";
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
                    JournalNo = current.JournalNo,
                    AccountCode = current.AccountCode,
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
}
