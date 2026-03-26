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
    private async Task LoadWorkspaceAsync(long? selectedJournalId = null, bool forceReload = false)
    {
        if (IsBusy && !forceReload)
        {
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "Memuat data jurnal...";

            var workspaceTask = _accessControlService.GetJournalWorkspaceDataAsync(_companyId, _locationId);
            var periodTask = _accessControlService.GetAccountingPeriodsAsync(_companyId, _locationId);
            await Task.WhenAll(workspaceTask, periodTask);

            var data = workspaceTask.Result;
            ReplaceCollection(Accounts, data.Accounts.OrderBy(x => x.Code));
            RefreshAccountLookup();
            ReplaceCollection(JournalList, data.Journals.OrderByDescending(x => x.JournalDate).ThenByDescending(x => x.Id));
            RaiseBrowseStateChanged();
            RefreshPeriodCache(periodTask.Result);
            UpdatePeriodStatusFromCache(JournalPeriodMonth);

            if (selectedJournalId.HasValue)
            {
                SelectedJournal = JournalList.FirstOrDefault(x => x.Id == selectedJournalId.Value);
                if (!IsBrowseSearchActive)
                {
                    SelectedBrowseJournal = SelectedJournal;
                }
            }

            _isLoaded = true;
            StatusMessage = "Data jurnal siap digunakan.";
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogError(
                nameof(JournalManagementViewModel),
                "LoadWorkspaceFailed",
                $"action=load_workspace company_id={_companyId} location_id={_locationId}",
                ex);
            StatusMessage = "Gagal memuat data jurnal.";
        }
        finally
        {
            IsBusy = false;
        }
    }


    private void NewJournal()
    {
        if (!CanCreateNewJournal)
        {
            StatusMessage = NewJournalTooltip;
            return;
        }

        JournalId = 0;
        JournalNo = string.Empty;
        JournalDate = DateTime.Today;
        JournalPeriodMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        ReferenceNo = string.Empty;
        JournalDescription = string.Empty;
        JournalStatus = "DRAFT";

        ReplaceInputLines(new[] { new JournalLineEditor { LineNo = 1 } });
        RecalculateTotals();

        ImportMessage = string.Empty;
        _stagedImportBundles = new List<JournalImportBundleResult>();
        ImportPreviewItems.Clear();
        SelectedJournalTabIndex = 0;

        StatusMessage = "Input jurnal baru siap.";
    }


    private async Task SaveDraftAsync()
    {
        if (!CanSaveDraft)
        {
            StatusMessage = SaveDraftTooltip;
            return;
        }

        if (IsBusy)
        {
            return;
        }

        if (!TryValidateDraftLines(out var payloadLines))
        {
            return;
        }

        var header = new ManagedJournalHeader
        {
            Id = JournalId,
            CompanyId = _companyId,
            LocationId = _locationId,
            JournalNo = JournalNo,
            JournalDate = JournalDate.Date,
            PeriodMonth = JournalPeriodMonth,
            ReferenceNo = ReferenceNo,
            Description = JournalDescription,
            Status = JournalStatus
        };

        try
        {
            IsBusy = true;
            var result = await _journalLifecycleWorkflow.SaveDraftAsync(header, payloadLines, _actorUsername);
            StatusMessage = result.Message;
            if (!result.IsSuccess)
            {
                return;
            }

            var savedId = result.EntityId;
            JournalId = savedId;
            JournalStatus = "DRAFT";

            await LoadWorkspaceAsync(savedId, forceReload: true);
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogError(
                nameof(JournalManagementViewModel),
                "SaveDraftFailed",
                $"action=save_draft company_id={_companyId} location_id={_locationId} journal_no={JournalNo}",
                ex);
            StatusMessage = "Gagal menyimpan draft jurnal.";
        }
        finally
        {
            IsBusy = false;
        }
    }


    private bool TryValidateDraftLines(out List<ManagedJournalLine> payloadLines)
    {
        payloadLines = new List<ManagedJournalLine>();

        var issues = new List<string>();
        JournalLineEditor? firstInvalidLine = null;

        foreach (var line in InputLines.OrderBy(x => x.LineNo))
        {
            SyncAccountLine(line);
            UpdateLineValidationState(line);

            if (!line.HasValidationError)
            {
                continue;
            }

            issues.Add($"baris {line.LineNo}: {line.ValidationMessage}");
            firstInvalidLine ??= line;
        }

        if (issues.Count == 0)
        {
            payloadLines = InputLines
                .Select(x => x.ToManaged())
                .ToList();
            return true;
        }

        SelectedInputLine = firstInvalidLine;
        StatusMessage = BuildDraftValidationMessage(issues);
        return false;
    }


    private static string BuildDraftValidationMessage(IReadOnlyList<string> issues)
    {
        const int maxShown = 4;
        var preview = string.Join("; ", issues.Take(maxShown));
        var remainder = issues.Count - maxShown;
        return remainder > 0
            ? $"Simpan draft dibatalkan. Perbaiki: {preview}; dan {remainder} masalah lain."
            : $"Simpan draft dibatalkan. Perbaiki: {preview}.";
    }


    private async Task PostCurrentAsync()
    {
        if (!CanPostCurrentJournal || IsBusy)
        {
            return;
        }

        await RunJournalStatusActionAsync(
            action: () => _journalLifecycleWorkflow.PostAsync(JournalId, _companyId, _locationId, _actorUsername),
            nextStatus: "POSTED",
            logEvent: "PostJournalFailed",
            logAction: "post_journal",
            fallbackMessage: "Gagal memposting jurnal.");
    }


    private async Task SubmitCurrentAsync()
    {
        if (!CanSubmitCurrentJournal || IsBusy)
        {
            return;
        }

        await RunJournalStatusActionAsync(
            action: () => _journalLifecycleWorkflow.SubmitAsync(JournalId, _companyId, _locationId, _actorUsername),
            nextStatus: "SUBMITTED",
            logEvent: "SubmitJournalFailed",
            logAction: "submit_journal",
            fallbackMessage: "Gagal submit jurnal.");
    }


    private async Task ApproveCurrentAsync()
    {
        if (!CanApproveCurrentJournal || IsBusy)
        {
            return;
        }

        await RunJournalStatusActionAsync(
            action: () => _journalLifecycleWorkflow.ApproveAsync(JournalId, _companyId, _locationId, _actorUsername),
            nextStatus: "APPROVED",
            logEvent: "ApproveJournalFailed",
            logAction: "approve_journal",
            fallbackMessage: "Gagal approve jurnal.");
    }


    private async Task RunJournalStatusActionAsync(
        Func<Task<JournalLifecycleResult>> action,
        string nextStatus,
        string logEvent,
        string logAction,
        string fallbackMessage)
    {
        try
        {
            IsBusy = true;
            var result = await action();
            StatusMessage = result.Message;
            if (!result.IsSuccess)
            {
                return;
            }

            JournalStatus = nextStatus;
            await LoadWorkspaceAsync(JournalId, forceReload: true);
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogError(
                nameof(JournalManagementViewModel),
                logEvent,
                $"action={logAction} journal_id={JournalId} company_id={_companyId} location_id={_locationId}",
                ex);
            StatusMessage = fallbackMessage;
        }
        finally
        {
            IsBusy = false;
        }
    }


    private async Task OpenSelectedJournalAsync()
    {
        var target = SelectedBrowseJournal ?? SelectedJournal;
        if (target is null || IsBusy)
        {
            return;
        }

        await OpenJournalAsync(target.Id);
    }


    private async Task OpenJournalAsync(long journalId)
    {
        try
        {
            IsBusy = true;
            var result = await _journalLifecycleWorkflow.OpenAsync(journalId, _companyId, _locationId);
            StatusMessage = result.Message;
            if (!result.IsSuccess || result.Bundle is null)
            {
                return;
            }

            ApplyOpenedJournalBundle(result.Bundle);
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogError(
                nameof(JournalManagementViewModel),
                "OpenJournalFailed",
                $"action=open_journal journal_id={journalId} company_id={_companyId} location_id={_locationId}",
                ex);
            StatusMessage = "Gagal memuat detail jurnal.";
        }
        finally
        {
            IsBusy = false;
        }
    }


    private void ApplyOpenedJournalBundle(ManagedJournalBundle bundle)
    {
        JournalId = bundle.Header.Id;
        JournalNo = bundle.Header.JournalNo;
        JournalDate = bundle.Header.JournalDate;
        JournalPeriodMonth = bundle.Header.PeriodMonth == default
            ? new DateTime(bundle.Header.JournalDate.Year, bundle.Header.JournalDate.Month, 1)
            : new DateTime(bundle.Header.PeriodMonth.Year, bundle.Header.PeriodMonth.Month, 1);
        ReferenceNo = bundle.Header.ReferenceNo;
        JournalDescription = bundle.Header.Description;
        JournalStatus = bundle.Header.Status;

        var editors = bundle.Lines.Select(JournalLineEditor.FromManaged).ToList();
        if (editors.Count == 0)
        {
            editors.Add(new JournalLineEditor { LineNo = 1 });
        }

        ReplaceInputLines(editors);
        RecalculateTotals();
        SelectedJournalTabIndex = 0;
    }


    private async Task SearchAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            var result = await _accessControlService.SearchJournalsAsync(
                _companyId,
                _locationId,
                new JournalSearchFilter
                {
                    PeriodMonth = SearchPeriodMonth,
                    DateFrom = SearchDateFrom,
                    DateTo = SearchDateTo,
                    Keyword = SearchKeyword,
                    Status = SearchStatus
                });

            SelectedBrowseJournal = null;
            ReplaceCollection(SearchResults, result);
            IsBrowseSearchActive = true;
            SelectedJournalTabIndex = 1;
            RaiseBrowseStateChanged();
            StatusMessage = $"Hasil pencarian: {SearchResults.Count} jurnal.";
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogError(
                nameof(JournalManagementViewModel),
                "SearchJournalsFailed",
                $"action=search_journal company_id={_companyId} location_id={_locationId} status={SearchStatus}",
                ex);
            StatusMessage = "Gagal melakukan pencarian jurnal.";
        }
        finally
        {
            IsBusy = false;
        }
    }


    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }

    private async Task RefreshBrowseAsync()
    {
        if (IsBrowseSearchActive || HasAnyBrowseFilters)
        {
            await SearchAsync();
            return;
        }

        await LoadWorkspaceAsync(forceReload: true);
    }

    private void ResetBrowseFilters()
    {
        ResetBrowseFilters(silent: false);
    }

    private void ResetBrowseFilters(bool silent)
    {
        _suppressBrowseFilterAutoSearch = true;
        SearchPeriodMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        SearchDateFrom = null;
        SearchDateTo = null;
        SearchStatus = string.Empty;
        SearchKeyword = string.Empty;
        _suppressBrowseFilterAutoSearch = false;

        if (silent)
        {
            SelectedBrowseJournal = null;
            SearchResults.Clear();
            IsBrowseSearchActive = false;
            _openSelectedJournalCommand.RaiseCanExecuteChanged();
            RaiseBrowseStateChanged();
            return;
        }

        _ = SearchAsync();
        StatusMessage = $"Filter jurnal direset ke periode {SearchPeriodMonth:MM/yyyy}.";
    }

    private void RaiseBrowseStateChanged()
    {
        OnPropertyChanged(nameof(BrowseJournals));
        OnPropertyChanged(nameof(BrowseResultSummary));
        OnPropertyChanged(nameof(HasBrowseResults));
        OnPropertyChanged(nameof(HasNoBrowseResults));
        OnPropertyChanged(nameof(BrowseEmptyStateTitle));
        OnPropertyChanged(nameof(BrowseEmptyStateDescription));
    }

}
