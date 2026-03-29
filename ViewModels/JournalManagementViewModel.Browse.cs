using System.Collections;
using Accounting.Infrastructure.Logging;
using Accounting.Services;

namespace Accounting.ViewModels;

public sealed partial class JournalManagementViewModel
{
    public void SetSelectedBrowseJournalRows(IEnumerable<JournalBrowseRowViewModel>? rows)
    {
        var normalized = rows?
            .Where(row => row is not null)
            .GroupBy(row => row.Id)
            .Select(group => group.First())
            .ToList() ?? new List<JournalBrowseRowViewModel>();

        ReplaceCollection(SelectedBrowseJournalRows, normalized);
        ActiveBrowseJournalRow = normalized.FirstOrDefault();
        SelectedBrowseJournal = normalized.FirstOrDefault()?.Summary;
        RaiseImportExportCommandCanExecuteChanged();
    }

    public async Task EnsureBrowseJournalDetailLoadedAsync(JournalBrowseRowViewModel? row)
    {
        if (row is null || row.IsDetailLoaded || row.IsDetailLoading)
        {
            return;
        }

        try
        {
            row.IsDetailLoading = true;
            row.DetailErrorMessage = string.Empty;

            var bundle = await _accessControlService.GetJournalBundleAsync(
                row.Id,
                EffectiveSearchCompanyId,
                EffectiveSearchLocationId,
                _actorUsername);

            row.Lines.Clear();
            if (bundle is null)
            {
                row.DetailErrorMessage = "Detail jurnal tidak tersedia.";
                return;
            }

            foreach (var line in bundle.Lines.OrderBy(line => line.LineNo))
            {
                row.Lines.Add(line);
            }

            row.IsDetailLoaded = true;
        }
        catch (Exception ex)
        {
            row.DetailErrorMessage = "Gagal memuat detail jurnal.";
            AppServices.Logger.LogError(
                nameof(JournalManagementViewModel),
                "LoadBrowseJournalDetailFailed",
                $"action=load_browse_journal_detail journal_id={row.Id} company_id={EffectiveSearchCompanyId} location_id={EffectiveSearchLocationId}",
                ex);
        }
        finally
        {
            row.IsDetailLoading = false;
        }
    }

    private void ReplaceBrowseJournalRows(IEnumerable<ManagedJournalSummary> summaries)
    {
        var activeRowId = ActiveBrowseJournalRow?.Id;
        var selectedIds = SelectedBrowseJournalRows
            .Select(row => row.Id)
            .ToHashSet();

        var rows = summaries
            .Select(summary => new JournalBrowseRowViewModel(summary))
            .ToList();

        ReplaceCollection(BrowseJournalRows, rows);

        var selectedRows = rows
            .Where(row => selectedIds.Contains(row.Id))
            .ToList();

        var activeRow = activeRowId.HasValue
            ? rows.FirstOrDefault(row => row.Id == activeRowId.Value)
            : null;

        if (activeRow is not null && selectedRows.All(row => row.Id != activeRow.Id))
        {
            selectedRows.Insert(0, activeRow);
        }

        if (selectedRows.Count == 0 && rows.Count > 0)
        {
            selectedRows.Add(rows[0]);
        }

        SetSelectedBrowseJournalRows(selectedRows);
    }

    private JournalBrowseRowViewModel? FindBrowseJournalRow(long journalId)
    {
        return BrowseJournalRows.FirstOrDefault(row => row.Id == journalId);
    }

    private static ManagedJournalSummary? ResolveBrowseJournalSummary(object? item)
    {
        return item switch
        {
            ManagedJournalSummary summary => summary,
            JournalBrowseRowViewModel row => row.Summary,
            _ => null
        };
    }

    private List<ManagedJournalSummary> ResolveSelectedJournals(object? parameter)
    {
        var selected = new List<ManagedJournalSummary>();
        if (parameter is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                var summary = ResolveBrowseJournalSummary(item);
                if (summary is not null)
                {
                    selected.Add(summary);
                }
            }
        }

        if (selected.Count == 0 && SelectedBrowseJournalRows.Count > 0)
        {
            selected.AddRange(SelectedBrowseJournalRows.Select(row => row.Summary));
        }

        if (selected.Count > 0)
        {
            return selected
                .GroupBy(summary => summary.Id)
                .Select(group => group.First())
                .ToList();
        }

        return SelectedJournal is null
            ? new List<ManagedJournalSummary>()
            : new List<ManagedJournalSummary> { SelectedJournal };
    }
}
