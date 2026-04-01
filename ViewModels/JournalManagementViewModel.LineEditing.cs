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
    private void AddLine()
    {
        AppendLineFrom(null);
    }


    public JournalLineEditor AppendLineFrom(JournalLineEditor? sourceLine)
    {
        var line = new JournalLineEditor
        {
            LineNo = InputLines.Count + 1,
            AccountCode = sourceLine?.AccountCode ?? string.Empty,
            AccountName = sourceLine?.AccountName ?? string.Empty,
            Description = sourceLine?.Description ?? string.Empty,
            Debit = 0m,
            Credit = 0m,
            DepartmentCode = sourceLine?.DepartmentCode ?? string.Empty,
            ProjectCode = sourceLine?.ProjectCode ?? string.Empty,
            SubledgerType = sourceLine?.SubledgerType ?? string.Empty,
            SubledgerId = sourceLine?.SubledgerId,
            SubledgerCode = sourceLine?.SubledgerCode ?? string.Empty,
            SubledgerName = sourceLine?.SubledgerName ?? string.Empty,
            CostCenterId = sourceLine?.CostCenterId,
            BlockId = sourceLine?.BlockId,
            CostCenterCode = sourceLine?.CostCenterCode ?? string.Empty
        };

        line.PropertyChanged += InputLineOnPropertyChanged;
        InputLines.Add(line);
        SyncAccountLine(line);
        SelectedInputLine = line;
        OnPropertyChanged(nameof(CanRemoveAnyLine));
        RecalculateTotals();

        return line;
    }


    private void RemoveSelectedLine()
    {
        RemoveLine(SelectedInputLine);
    }


    private void RemoveLineByRow(object? parameter)
    {
        RemoveLine(parameter as JournalLineEditor);
    }


    private void RemoveLine(JournalLineEditor? line)
    {
        if (line is null || InputLines.Count <= 1)
        {
            return;
        }

        var removedIndex = InputLines.IndexOf(line);
        if (removedIndex < 0)
        {
            return;
        }

        line.PropertyChanged -= InputLineOnPropertyChanged;
        InputLines.RemoveAt(removedIndex);
        ReindexLines();
        OnPropertyChanged(nameof(CanRemoveAnyLine));

        if (InputLines.Count > 0)
        {
            var nextIndex = Math.Clamp(removedIndex, 0, InputLines.Count - 1);
            SelectedInputLine = InputLines[nextIndex];
        }

        RecalculateTotals();
    }


    public IReadOnlyList<JournalLineEditor> MoveInputLines(
        IReadOnlyList<JournalLineEditor> movingLines,
        int targetIndex,
        bool insertBefore = true)
    {
        if (movingLines is null || movingLines.Count == 0)
        {
            return Array.Empty<JournalLineEditor>();
        }

        var orderedMovingLines = movingLines
            .Where(line => line is not null && InputLines.Contains(line))
            .Distinct()
            .OrderBy(line => InputLines.IndexOf(line))
            .ToList();

        if (orderedMovingLines.Count == 0)
        {
            return Array.Empty<JournalLineEditor>();
        }

        var normalizedTarget = Math.Clamp(targetIndex, 0, InputLines.Count - 1);
        var dropIndex = insertBefore ? normalizedTarget : normalizedTarget + 1;
        var sourceIndexes = orderedMovingLines
            .Select(line => InputLines.IndexOf(line))
            .Where(index => index >= 0)
            .OrderBy(index => index)
            .ToList();

        if (sourceIndexes.Count == 0)
        {
            return Array.Empty<JournalLineEditor>();
        }

        var firstSourceIndex = sourceIndexes[0];
        var lastSourceIndex = sourceIndexes[^1];
        if (dropIndex >= firstSourceIndex && dropIndex <= lastSourceIndex + 1)
        {
            return orderedMovingLines;
        }

        var adjustedDropIndex = dropIndex - sourceIndexes.Count(index => index < dropIndex);
        adjustedDropIndex = Math.Clamp(adjustedDropIndex, 0, InputLines.Count - orderedMovingLines.Count);

        foreach (var line in orderedMovingLines)
        {
            InputLines.Remove(line);
        }

        for (var i = 0; i < orderedMovingLines.Count; i++)
        {
            InputLines.Insert(adjustedDropIndex + i, orderedMovingLines[i]);
        }

        ReindexLines();
        SelectedInputLine = orderedMovingLines.FirstOrDefault();
        OnPropertyChanged(nameof(CanRemoveAnyLine));
        RecalculateTotals();

        return orderedMovingLines;
    }


    private void OpenAccountPicker(object? parameter)
    {
        var targetLine = parameter as JournalLineEditor ?? SelectedInputLine;
        if (!TryPrepareAccountPicker(targetLine, out var activeAccounts, out var initialFilter))
        {
            return;
        }

        var picker = new Accounting.AccountSelectionWindow(activeAccounts, initialFilter)
        {
            Owner = Application.Current?.MainWindow
        };

        if (picker.ShowDialog() != true || picker.SelectedAccount is null)
        {
            return;
        }

        ApplySelectedAccountToLine(targetLine, picker.SelectedAccount);
    }


    private void OpenBlockPicker(object? parameter)
    {
        var targetLine = parameter as JournalLineEditor ?? SelectedInputLine;
        if (!TryPrepareBlockPicker(targetLine, out var activeBlocks, out var initialFilter))
        {
            return;
        }

        var picker = new Accounting.BlockSelectionWindow(activeBlocks, initialFilter)
        {
            Owner = Application.Current?.MainWindow
        };

        if (picker.ShowDialog() != true || picker.SelectedBlock is null)
        {
            return;
        }

        ApplySelectedBlockToLine(targetLine, picker.SelectedBlock);
    }


    private void OpenSubledgerPicker(object? parameter)
    {
        var targetLine = parameter as JournalLineEditor ?? SelectedInputLine;
        if (!TryPrepareSubledgerPicker(targetLine, out var activeSubledgers, out var initialFilter, out var title))
        {
            return;
        }

        var picker = new Accounting.SubledgerSelectionWindow(title, activeSubledgers, initialFilter)
        {
            Owner = Application.Current?.MainWindow
        };

        if (picker.ShowDialog() != true || picker.SelectedSubledger is null)
        {
            return;
        }

        ApplySelectedSubledgerToLine(targetLine, picker.SelectedSubledger);
    }


    public bool TryPrepareAccountPicker(
        JournalLineEditor? targetLine,
        out IReadOnlyList<ManagedAccount> activeAccounts,
        out string initialFilter)
    {
        initialFilter = targetLine?.AccountCode ?? string.Empty;

        if (targetLine is null)
        {
            StatusMessage = "Pilih baris jurnal terlebih dahulu.";
            activeAccounts = Array.Empty<ManagedAccount>();
            return false;
        }

        var accounts = Accounts
            .Where(x => x.IsActive)
            .OrderBy(x => x.Code)
            .ToList();

        if (accounts.Count == 0)
        {
            StatusMessage = "Tidak ada akun aktif yang bisa dipilih.";
            activeAccounts = Array.Empty<ManagedAccount>();
            return false;
        }

        activeAccounts = accounts;
        return true;
    }


    public bool ApplySelectedAccountToLine(JournalLineEditor? targetLine, ManagedAccount? selectedAccount)
    {
        if (targetLine is null || selectedAccount is null)
        {
            return false;
        }

        SelectedInputLine = targetLine;
        targetLine.AccountCode = selectedAccount.Code;
        targetLine.AccountName = selectedAccount.Name;
        SyncAccountLine(targetLine);
        UpdateLineValidationState(targetLine);
        return true;
    }


    public bool TryPrepareBlockPicker(
        JournalLineEditor? targetLine,
        out IReadOnlyList<ManagedCostCenter> activeBlocks,
        out string initialFilter)
    {
        initialFilter = targetLine?.CostCenterCode ?? string.Empty;

        if (targetLine is null)
        {
            StatusMessage = "Pilih baris jurnal terlebih dahulu.";
            activeBlocks = Array.Empty<ManagedCostCenter>();
            return false;
        }

        var blocks = BlockCostCenters
            .Where(x => x.IsActive)
            .OrderBy(x => x.CostCenterCode)
            .ToList();

        if (blocks.Count == 0)
        {
            StatusMessage = "Tidak ada blok aktif yang bisa dipilih.";
            activeBlocks = Array.Empty<ManagedCostCenter>();
            return false;
        }

        activeBlocks = blocks;
        return true;
    }


    public bool ApplySelectedBlockToLine(JournalLineEditor? targetLine, ManagedCostCenter? selectedBlock)
    {
        if (targetLine is null || selectedBlock is null)
        {
            return false;
        }

        SelectedInputLine = targetLine;
        targetLine.CostCenterId = null;
        targetLine.BlockId = selectedBlock.BlockId ?? selectedBlock.Id;
        targetLine.CostCenterCode = selectedBlock.CostCenterCode;
        UpdateLineValidationState(targetLine);
        return true;
    }


    public bool TryPrepareSubledgerPicker(
        JournalLineEditor? targetLine,
        out IReadOnlyList<ManagedSubledgerReference> activeSubledgers,
        out string initialFilter,
        out string title)
    {
        initialFilter = targetLine?.SubledgerCode ?? string.Empty;
        title = "Pilih Buku Bantu";

        if (targetLine is null)
        {
            StatusMessage = "Pilih baris jurnal terlebih dahulu.";
            activeSubledgers = Array.Empty<ManagedSubledgerReference>();
            return false;
        }

        if (!_accountLookupByCode.TryGetValue(targetLine.AccountCode ?? string.Empty, out var account) ||
            !account.RequiresSubledger)
        {
            StatusMessage = "Akun pada baris ini tidak membutuhkan buku bantu.";
            activeSubledgers = Array.Empty<ManagedSubledgerReference>();
            return false;
        }

        var requiredType = account.AllowedSubledgerType?.Trim().ToUpperInvariant() ?? string.Empty;
        IReadOnlyList<ManagedSubledgerReference> candidates = requiredType switch
        {
            "VENDOR" => Vendors.Where(x => x.IsActive).OrderBy(x => x.Code).ToList(),
            "CUSTOMER" => Customers.Where(x => x.IsActive).OrderBy(x => x.Code).ToList(),
            "EMPLOYEE" => Employees.Where(x => x.IsActive).OrderBy(x => x.Code).ToList(),
            _ => Array.Empty<ManagedSubledgerReference>()
        };

        title = requiredType switch
        {
            "VENDOR" => "Pilih Vendor",
            "CUSTOMER" => "Pilih Customer",
            "EMPLOYEE" => "Pilih Karyawan",
            _ => "Pilih Buku Bantu"
        };

        if (candidates.Count == 0)
        {
            StatusMessage = $"Tidak ada master {title.Replace("Pilih ", string.Empty).ToLowerInvariant()} aktif yang bisa dipilih.";
            activeSubledgers = Array.Empty<ManagedSubledgerReference>();
            return false;
        }

        activeSubledgers = candidates;
        return true;
    }


    public bool ApplySelectedSubledgerToLine(JournalLineEditor? targetLine, ManagedSubledgerReference? selectedSubledger)
    {
        if (targetLine is null || selectedSubledger is null)
        {
            return false;
        }

        SelectedInputLine = targetLine;
        targetLine.SubledgerType = selectedSubledger.SubledgerType;
        targetLine.SubledgerId = selectedSubledger.Id;
        targetLine.SubledgerCode = selectedSubledger.Code;
        targetLine.SubledgerName = selectedSubledger.Name;
        UpdateLineValidationState(targetLine);
        return true;
    }


    private void InputLineOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not JournalLineEditor line)
        {
            return;
        }

        if (e.PropertyName == nameof(JournalLineEditor.AccountCode))
        {
            SyncAccountLine(line);
        }
        else if (e.PropertyName is
            nameof(JournalLineEditor.Description) or
            nameof(JournalLineEditor.Debit) or
            nameof(JournalLineEditor.Credit) or
            nameof(JournalLineEditor.DepartmentCode) or
            nameof(JournalLineEditor.ProjectCode) or
            nameof(JournalLineEditor.SubledgerCode) or
            nameof(JournalLineEditor.CostCenterCode))
        {
            UpdateLineValidationState(line);
        }

        if (e.PropertyName is nameof(JournalLineEditor.Debit) or nameof(JournalLineEditor.Credit))
        {
            RecalculateTotals();
        }
    }


    private void ReplaceInputLines(IEnumerable<JournalLineEditor> items)
    {
        foreach (var line in InputLines)
        {
            line.PropertyChanged -= InputLineOnPropertyChanged;
        }

        InputLines.Clear();

        foreach (var line in items)
        {
            line.PropertyChanged += InputLineOnPropertyChanged;
            InputLines.Add(line);
            SyncAccountLine(line);
        }

        ReindexLines();
        SelectedInputLine = InputLines.FirstOrDefault();
        OnPropertyChanged(nameof(CanRemoveAnyLine));
    }


    private void ReindexLines()
    {
        for (var i = 0; i < InputLines.Count; i++)
        {
            InputLines[i].LineNo = i + 1;
        }
    }


    private void RecalculateTotals()
    {
        TotalDebit = InputLines.Sum(x => Math.Round(x.Debit, 2));
        TotalCredit = InputLines.Sum(x => Math.Round(x.Credit, 2));
    }


    private void RefreshAccountLookup()
    {
        _accountLookupByCode.Clear();
        var rebuiltLookup = _lineValidationService.BuildActiveAccountLookup(Accounts);
        foreach (var pair in rebuiltLookup)
        {
            _accountLookupByCode[pair.Key] = pair.Value;
        }

        foreach (var line in InputLines)
        {
            SyncAccountLine(line);
        }
    }


    private void SyncAccountLine(JournalLineEditor line)
    {
        if (!_accountSyncInProgress.Add(line))
        {
            return;
        }

        try
        {
            _lineValidationService.SyncAccountLine(line, _accountLookupByCode, _costCenterLookupByCode, _subledgerLookupByKey);
        }
        finally
        {
            _accountSyncInProgress.Remove(line);
        }
    }


    private void UpdateLineValidationState(JournalLineEditor line)
    {
        _lineValidationService.ValidateLine(line, _accountLookupByCode, _costCenterLookupByCode, _subledgerLookupByKey);
    }


}
