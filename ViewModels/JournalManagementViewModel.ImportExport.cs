using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using Microsoft.Win32;
using Accounting.Infrastructure.Logging;
using Accounting.Services;

namespace Accounting.ViewModels;

public sealed partial class JournalManagementViewModel
{
    private void BrowseImportFile()
    {
        if (!CanBrowseImportFile)
        {
            ImportMessage = BrowseImportTooltip;
            StatusMessage = BrowseImportTooltip;
            return;
        }

        var dialog = new OpenFileDialog
        {
            Filter = "Excel Workbook (*.xlsx)|*.xlsx",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
        {
            ImportFilePath = dialog.FileName;
        }
    }


    private void PreviewImport()
    {
        if (!CanPreviewImportFile)
        {
            ImportMessage = PreviewImportTooltip;
            StatusMessage = PreviewImportTooltip;
            return;
        }

        if (_accountLookupByCode.Count == 0)
        {
            ImportMessage = "Data COA belum tersedia untuk validasi import.";
            _stagedImportBundles = new List<JournalImportBundleResult>();
            ImportPreviewItems.Clear();
            return;
        }

        var enriched = _importExportWorkflow.PreviewImport(ImportFilePath, _accountLookupByCode);

        ImportMessage = enriched.Message;
        ReplaceCollection(ImportPreviewItems, enriched.PreviewItems);
        _stagedImportBundles = enriched.JournalBundles
            .Where(x => x.IsValid && x.Lines.Count > 0)
            .ToList();
        RaiseImportExportStateChanged();
    }


    private async Task CommitImportAsync()
    {
        if (!CanCommitImportDrafts)
        {
            ImportMessage = CommitImportTooltip;
            StatusMessage = CommitImportTooltip;
            return;
        }

        if (IsBusy)
        {
            return;
        }

        if (_stagedImportBundles.Count == 0)
        {
            ImportMessage = "Tidak ada jurnal valid untuk diimport. Jalankan preview lalu lihat kolom Pesan untuk detail kegagalan.";
            return;
        }

        try
        {
            IsBusy = true;
            var commitResult = await _importExportWorkflow.CommitImportAsync(
                _stagedImportBundles,
                _companyId,
                _locationId,
                _actorUsername);

            ImportMessage = commitResult.Message;
            StatusMessage = commitResult.StatusMessage;

            if (commitResult.FirstSavedId > 0)
            {
                await LoadWorkspaceAsync(commitResult.FirstSavedId, forceReload: true);
                await OpenJournalAsync(commitResult.FirstSavedId);
            }
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogError(
                nameof(JournalManagementViewModel),
                "CommitImportFailed",
                $"action=commit_import company_id={_companyId} location_id={_locationId} file_path={ImportFilePath}",
                ex);
            ImportMessage = "Gagal import draft jurnal.";
            StatusMessage = "Gagal import draft jurnal.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task PullInventoryJournalsAsync()
    {
        if (!CanPullInventoryJournals)
        {
            InventoryPullMessage = InventoryPullTooltip;
            StatusMessage = InventoryPullTooltip;
            return;
        }

        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            var periodMonth = new DateTime(InventoryPullPeriodMonth.Year, InventoryPullPeriodMonth.Month, 1);
            InventoryPullMessage = $"Menarik jurnal inventory periode {periodMonth:yyyy-MM}...";

            var result = await _accessControlService.PullInventoryJournalsForPeriodAsync(
                _companyId,
                _locationId,
                periodMonth,
                _actorUsername);

            InventoryPullMessage = result.Message;
            StatusMessage = result.Message;

            var pulledJournalNos = ParsePulledDraftJournalNos(result.Message);
            ReplaceCollection(InventoryPullCreatedJournalNos, pulledJournalNos);

            if (result.IsSuccess)
            {
                await LoadWorkspaceAsync(forceReload: true);
            }
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogError(
                nameof(JournalManagementViewModel),
                "PullInventoryJournalsFailed",
                $"action=pull_inventory_journals company_id={_companyId} location_id={_locationId} period={InventoryPullPeriodMonth:yyyy-MM}",
                ex);
            InventoryPullMessage = "Gagal menarik jurnal inventory.";
            StatusMessage = "Gagal menarik jurnal inventory.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadPulledDraftsToSearchAsync()
    {
        var monthStart = new DateTime(InventoryPullPeriodMonth.Year, InventoryPullPeriodMonth.Month, 1);

        _suppressBrowseFilterAutoSearch = true;
        SearchPeriodMonth = monthStart;
        SearchDateFrom = null;
        SearchDateTo = null;
        SearchStatus = "DRAFT";
        SearchKeyword = string.Empty;
        _suppressBrowseFilterAutoSearch = false;

        await SearchAsync();
        SelectedJournalTabIndex = 1;

        if (SearchResults.Count == 0)
        {
            InventoryPullMessage = $"Tidak ada draft jurnal periode {monthStart:yyyy-MM} di hasil pencarian.";
            return;
        }

        InventoryPullMessage = $"Draft jurnal periode {monthStart:yyyy-MM} dimuat di daftar jurnal ({SearchResults.Count} data).";
    }

    private async Task OpenPulledDraftJournalAsync(object? parameter)
    {
        var targetJournalNo = (parameter as string)?.Trim();
        if (string.IsNullOrWhiteSpace(targetJournalNo))
        {
            targetJournalNo = InventoryPullCreatedJournalNos.FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(targetJournalNo))
        {
            InventoryPullMessage = "Belum ada nomor jurnal draft hasil pull untuk dibuka.";
            return;
        }

        if (IsBusy)
        {
            return;
        }

        var existing = JournalList.FirstOrDefault(
            x => string.Equals(x.JournalNo, targetJournalNo, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            await LoadWorkspaceAsync(forceReload: true);
            existing = JournalList.FirstOrDefault(
                x => string.Equals(x.JournalNo, targetJournalNo, StringComparison.OrdinalIgnoreCase));
        }

        if (existing is null)
        {
            InventoryPullMessage = $"Draft jurnal {targetJournalNo} belum ditemukan di daftar jurnal.";
            return;
        }

        await OpenJournalAsync(existing.Id);
        InventoryPullMessage = $"Draft jurnal {targetJournalNo} dibuka di editor jurnal.";
    }

    private static IReadOnlyCollection<string> ParsePulledDraftJournalNos(string message)
    {
        var rawMessage = message ?? string.Empty;
        var marker = "Draft dibuat:";
        var markerIndex = rawMessage.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return Array.Empty<string>();
        }

        var payload = rawMessage[(markerIndex + marker.Length)..].Trim();
        if (string.IsNullOrWhiteSpace(payload))
        {
            return Array.Empty<string>();
        }

        return payload
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }


    private void ExportCurrentJournal()
    {
        if (!CanExportCurrentJournal)
        {
            StatusMessage = ExportCurrentTooltip;
            return;
        }

        var lines = InputLines.Select(x => x.ToManaged()).ToList();
        var header = new ManagedJournalHeader
        {
            Id = JournalId,
            CompanyId = _companyId,
            LocationId = _locationId,
            JournalNo = JournalNo,
            JournalDate = JournalDate,
            PeriodMonth = JournalPeriodMonth,
            ReferenceNo = ReferenceNo,
            Description = JournalDescription,
            Status = JournalStatus
        };

        var filePath = AskSavePath(header.JournalNo);
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        try
        {
            _importExportWorkflow.ExportCurrent(filePath, header, lines);
            StatusMessage = "Export jurnal berhasil.";
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogError(
                nameof(JournalManagementViewModel),
                "ExportCurrentJournalFailed",
                $"action=export_current_journal journal_id={JournalId} company_id={_companyId} location_id={_locationId}",
                ex);
            StatusMessage = "Gagal export jurnal aktif.";
        }
    }


    private async void ExportSelectedJournal(object? parameter)
    {
        if (!CanExportSelectedJournals(parameter))
        {
            StatusMessage = _canExportJournals
                ? "Pilih jurnal dari tab Daftar terlebih dahulu."
                : ExportSelectedTooltip;
            return;
        }

        var selectedSummaries = ResolveSelectedJournals(parameter);
        try
        {
            var filePath = selectedSummaries.Count > 1
                ? AskSavePathForMulti(selectedSummaries.Count)
                : AskSavePath(selectedSummaries[0].JournalNo);
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            var exportResult = await _importExportWorkflow.ExportSelectedAsync(
                selectedSummaries,
                _companyId,
                _locationId,
                filePath);

            StatusMessage = exportResult.Message;
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogError(
                nameof(JournalManagementViewModel),
                "ExportSelectedJournalFailed",
                $"action=export_selected_journal company_id={_companyId} location_id={_locationId}",
                ex);
            StatusMessage = "Gagal export jurnal terpilih.";
        }
    }

    private async Task PreviewExportPeriodAsync()
    {
        if (!CanPreviewExportPeriod)
        {
            StatusMessage = PreviewExportPeriodTooltip;
            return;
        }

        var (periodStart, periodEnd, periodKey) = GetSelectedExportPeriod();

        try
        {
            IsBusy = true;
            ResetExportPeriodPreview(clearPeriodKey: false);

            var summaries = await _accessControlService.SearchJournalsAsync(
                _companyId,
                _locationId,
                new JournalSearchFilter
                {
                    DateFrom = periodStart,
                    DateTo = periodEnd,
                    Keyword = string.Empty,
                    Status = string.Empty
                });

            if (summaries.Count == 0)
            {
                _exportPeriodPreviewKey = periodKey;
                ExportPeriodPreviewSummary = $"Periode {periodStart:yyyy-MM}: tidak ada jurnal.";
                StatusMessage = ExportPeriodPreviewSummary;
                return;
            }

            var orderedSummaries = summaries
                .OrderBy(x => x.JournalDate)
                .ThenBy(x => x.JournalNo, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Id)
                .ToList();
            ReplaceCollection(ExportPeriodPreviewJournals, orderedSummaries);

            var previewLines = new List<JournalExportPreviewLine>();
            foreach (var summary in orderedSummaries)
            {
                var bundle = await _accessControlService.GetJournalBundleAsync(summary.Id, _companyId, _locationId);
                if (bundle is null)
                {
                    continue;
                }

                foreach (var line in bundle.Lines.OrderBy(x => x.LineNo))
                {
                    previewLines.Add(new JournalExportPreviewLine
                    {
                        JournalId = summary.Id,
                        JournalNo = summary.JournalNo,
                        JournalDate = summary.JournalDate,
                        ReferenceNo = summary.ReferenceNo,
                        JournalStatus = summary.Status,
                        LineNo = line.LineNo,
                        AccountCode = line.AccountCode,
                        AccountName = line.AccountName,
                        Description = line.Description,
                        Debit = line.Debit,
                        Credit = line.Credit,
                        DepartmentCode = line.DepartmentCode,
                        ProjectCode = line.ProjectCode,
                        CostCenterCode = line.CostCenterCode
                    });
                }
            }

            ReplaceCollection(ExportPeriodPreviewLines, previewLines);

            _exportPeriodPreviewKey = periodKey;
            ExportPeriodPreviewSummary = $"Periode {periodStart:yyyy-MM}: {ExportPeriodPreviewJournals.Count} jurnal, {ExportPeriodPreviewLines.Count} baris detail.";
            StatusMessage = ExportPeriodPreviewSummary;
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogError(
                nameof(JournalManagementViewModel),
                "PreviewExportPeriodJournalFailed",
                $"action=preview_export_period_journal company_id={_companyId} location_id={_locationId} period={periodStart:yyyy-MM}",
                ex);
            ExportPeriodPreviewSummary = "Gagal memuat detail jurnal periode.";
            StatusMessage = "Gagal memuat detail jurnal periode.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExportPeriodAsync()
    {
        if (!CanExportPeriod)
        {
            StatusMessage = !_canExportJournals
                ? ExportPeriodTooltip
                : $"Tampilkan detail jurnal periode {GetSelectedExportPeriod().PeriodStart:yyyy-MM} terlebih dahulu sebelum export.";
            return;
        }

        var (periodStart, _, periodKey) = GetSelectedExportPeriod();
        if (!HasExportPeriodPreview || !string.Equals(_exportPeriodPreviewKey, periodKey, StringComparison.Ordinal))
        {
            StatusMessage = $"Tampilkan detail jurnal periode {periodStart:yyyy-MM} terlebih dahulu sebelum export.";
            return;
        }

        var summaries = ExportPeriodPreviewJournals.ToList();
        if (summaries.Count == 0)
        {
            StatusMessage = $"Periode {periodStart:yyyy-MM} tidak memiliki jurnal untuk diexport.";
            return;
        }

        try
        {
            IsBusy = true;
            var useLegacyFormat = ExportPeriodUseLegacyFormat;
            var filePath = AskSavePathForPeriod(periodStart, summaries.Count, useLegacyFormat);
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            var exportResult = await _importExportWorkflow.ExportSelectedAsync(
                summaries,
                _companyId,
                _locationId,
                filePath,
                useLegacyFormat ? JournalExportLayout.HeaderDetailLegacy : JournalExportLayout.FlatJournals);
            StatusMessage = exportResult.Message;
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogError(
                nameof(JournalManagementViewModel),
                "ExportPeriodJournalFailed",
                $"action=export_period_journal company_id={_companyId} location_id={_locationId} period={periodStart:yyyy-MM}",
                ex);
            StatusMessage = "Gagal export jurnal periode.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private (DateTime PeriodStart, DateTime PeriodEnd, string PeriodKey) GetSelectedExportPeriod()
    {
        var month = Math.Clamp(ExportPeriodMonth, 1, 12);
        var year = Math.Max(2000, ExportPeriodYear);
        var periodStart = new DateTime(year, month, 1);
        var periodEnd = periodStart.AddMonths(1).AddDays(-1);
        return (periodStart, periodEnd, $"{year:D4}-{month:D2}");
    }

    private void ResetExportPeriodPreview(bool clearPeriodKey = true)
    {
        ExportPeriodPreviewJournals.Clear();
        ExportPeriodPreviewLines.Clear();
        ExportPeriodPreviewSummary = "Pilih periode lalu klik Tampilkan Detail.";
        if (clearPeriodKey)
        {
            _exportPeriodPreviewKey = string.Empty;
        }
    }


    private List<ManagedJournalSummary> ResolveSelectedJournals(object? parameter)
    {
        var selected = new List<ManagedJournalSummary>();
        if (parameter is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                if (item is ManagedJournalSummary summary)
                {
                    selected.Add(summary);
                }
            }
        }

        if (selected.Count > 0)
        {
            return selected
                .GroupBy(x => x.Id)
                .Select(x => x.First())
                .ToList();
        }

        return SelectedJournal is null
            ? new List<ManagedJournalSummary>()
            : new List<ManagedJournalSummary> { SelectedJournal };
    }


    private static string AskSavePath(string journalNo)
    {
        var safeNo = string.IsNullOrWhiteSpace(journalNo) ? "JOURNAL" : journalNo.Trim();
        var dialog = new SaveFileDialog
        {
            Filter = "Excel Workbook (*.xlsx)|*.xlsx",
            DefaultExt = "xlsx",
            AddExtension = true,
            FileName = $"JURNAL_{safeNo}_{DateTime.Now:yyyyMMddHHmmss}.xlsx"
        };

        return dialog.ShowDialog() == true ? dialog.FileName : string.Empty;
    }


    private static string AskSavePathForMulti(int journalCount)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Excel Workbook (*.xlsx)|*.xlsx",
            DefaultExt = "xlsx",
            AddExtension = true,
            FileName = $"JURNAL_MULTI_{journalCount}_{DateTime.Now:yyyyMMddHHmmss}.xlsx"
        };

        return dialog.ShowDialog() == true ? dialog.FileName : string.Empty;
    }

    private static string AskSavePathForPeriod(DateTime periodStart, int journalCount, bool useLegacyFormat)
    {
        var formatSuffix = useLegacyFormat ? "_LEGACY" : string.Empty;
        var dialog = new SaveFileDialog
        {
            Filter = "Excel Workbook (*.xlsx)|*.xlsx",
            DefaultExt = "xlsx",
            AddExtension = true,
            FileName = $"JURNAL_PERIODE_{periodStart:yyyyMM}_{journalCount}{formatSuffix}_{DateTime.Now:yyyyMMddHHmmss}.xlsx"
        };

        return dialog.ShowDialog() == true ? dialog.FileName : string.Empty;
    }


}
