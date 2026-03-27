using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using Microsoft.Win32;
using Accounting.Infrastructure.Logging;
using Accounting.Services;

namespace Accounting.ViewModels;

public sealed partial class JournalManagementViewModel
{
    private async Task RefreshPeriodStatusForDateAsync(DateTime date, bool reloadFromService)
    {
        try
        {
            if (reloadFromService)
            {
                var periods = await _accessControlService.GetAccountingPeriodsAsync(_companyId, _locationId);
                RefreshPeriodCache(periods);
            }
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogWarning(
                nameof(JournalManagementViewModel),
                "RefreshPeriodStatusFailed",
                $"action=refresh_period_cache company_id={_companyId} location_id={_locationId}",
                ex);
            // Keep last known period status cache when refresh fails.
        }

        UpdatePeriodStatusFromCache(date);
    }


    private void RefreshPeriodCache(IEnumerable<ManagedAccountingPeriod> periods)
    {
        _periodOpenByMonthKey.Clear();

        var options = periods
            .Where(period => period.PeriodMonth != default)
            .GroupBy(period => GetPeriodKey(period.PeriodMonth))
            .Select(group => group
                .OrderByDescending(period => period.PeriodMonth)
                .First())
            .Select(period =>
            {
                var normalized = new DateTime(period.PeriodMonth.Year, period.PeriodMonth.Month, 1);
                var key = GetPeriodKey(normalized);
                _periodOpenByMonthKey[key] = period.IsOpen;
                return new JournalAccountingPeriodOption(normalized, period.IsOpen);
            })
            .OrderByDescending(option => option.PeriodMonth)
            .ToList();

        ReplaceCollection(AccountingPeriodOptions, options);
        SyncJournalPeriodPickerState();
        SyncSearchPeriodPickerState();
        SyncInventoryPullPeriodPickerState();
    }


    private void UpdatePeriodStatusFromCache(DateTime date)
    {
        var target = date.Date;
        var key = GetPeriodKey(target);
        var isOpen = !_periodOpenByMonthKey.TryGetValue(key, out var mappedOpen) || mappedOpen;
        IsCurrentPeriodOpen = isOpen;
        CurrentPeriodStatusText = isOpen ? "OPEN" : "CLOSED";
        CurrentPeriodMonthText = target.ToString("MM/yyyy");
    }


    private static string GetPeriodKey(DateTime date)
    {
        return $"{date.Year:D4}-{date.Month:D2}";
    }

    private void SyncJournalPeriodPickerState()
    {
        SyncPeriodPickerState(
            JournalPeriodMonth,
            nameof(JournalPeriodText),
            nameof(SelectedJournalAccountingPeriodOption),
            ref _journalPeriodText,
            ref _selectedJournalAccountingPeriodOption);
    }

    private void SyncSearchPeriodPickerState()
    {
        SyncPeriodPickerState(
            SearchPeriodMonth,
            nameof(SearchPeriodText),
            nameof(SelectedSearchAccountingPeriodOption),
            ref _searchPeriodText,
            ref _selectedSearchAccountingPeriodOption);
        SyncSearchPeriodCalendarState();
        OnPropertyChanged(nameof(SearchAccountingPeriodDisplayText));
    }

    private void SyncInventoryPullPeriodPickerState()
    {
        SyncPeriodPickerState(
            InventoryPullPeriodMonth,
            nameof(InventoryPullPeriodText),
            nameof(SelectedInventoryPullAccountingPeriodOption),
            ref _inventoryPullPeriodText,
            ref _selectedInventoryPullAccountingPeriodOption);
    }

    private void SyncPeriodPickerState(
        DateTime periodMonth,
        string textPropertyName,
        string selectedOptionPropertyName,
        ref string textField,
        ref JournalAccountingPeriodOption? selectedOptionField)
    {
        _isSynchronizingPeriodPickerState = true;
        try
        {
            var normalized = new DateTime(periodMonth.Year, periodMonth.Month, 1);
            var matchingOption = AccountingPeriodOptions.FirstOrDefault(option => option.PeriodMonth.Date == normalized.Date);
            var normalizedText = normalized.ToString("MM/yyyy", CultureInfo.InvariantCulture);

            if (!string.Equals(textField, normalizedText, StringComparison.Ordinal))
            {
                textField = normalizedText;
                OnPropertyChanged(textPropertyName);
            }

            if (!ReferenceEquals(selectedOptionField, matchingOption))
            {
                selectedOptionField = matchingOption;
                OnPropertyChanged(selectedOptionPropertyName);
            }
        }
        finally
        {
            _isSynchronizingPeriodPickerState = false;
        }
    }

    private void ApplyJournalPeriodText(string value)
    {
        if (_isSynchronizingPeriodPickerState)
        {
            return;
        }

        ApplyPeriodText(value, JournalPeriodMonth, month => JournalPeriodMonth = month, ref _selectedJournalAccountingPeriodOption, nameof(SelectedJournalAccountingPeriodOption));
    }

    private void ApplySearchPeriodText(string value)
    {
        if (_isSynchronizingPeriodPickerState)
        {
            return;
        }

        ApplyPeriodText(value, SearchPeriodMonth, month => SearchPeriodMonth = month, ref _selectedSearchAccountingPeriodOption, nameof(SelectedSearchAccountingPeriodOption));
    }

    private void ApplyInventoryPullPeriodText(string value)
    {
        if (_isSynchronizingPeriodPickerState)
        {
            return;
        }

        ApplyPeriodText(value, InventoryPullPeriodMonth, month => InventoryPullPeriodMonth = month, ref _selectedInventoryPullAccountingPeriodOption, nameof(SelectedInventoryPullAccountingPeriodOption));
    }

    private void ApplyPeriodText(
        string value,
        DateTime currentMonth,
        Action<DateTime> assignMonth,
        ref JournalAccountingPeriodOption? selectedOptionField,
        string selectedOptionPropertyName)
    {
        if (!TryParsePeriodMonthText(value, out var parsedMonth))
        {
            if (selectedOptionField is not null)
            {
                _isSynchronizingPeriodPickerState = true;
                try
                {
                    selectedOptionField = null;
                    OnPropertyChanged(selectedOptionPropertyName);
                }
                finally
                {
                    _isSynchronizingPeriodPickerState = false;
                }
            }

            return;
        }

        if (parsedMonth != currentMonth)
        {
            assignMonth(parsedMonth);
            return;
        }

        if (selectedOptionField is not null && selectedOptionField.PeriodMonth.Date != parsedMonth.Date)
        {
            _isSynchronizingPeriodPickerState = true;
            try
            {
                selectedOptionField = null;
                OnPropertyChanged(selectedOptionPropertyName);
            }
            finally
            {
                _isSynchronizingPeriodPickerState = false;
            }
        }
    }

    private static bool TryParsePeriodMonthText(string value, out DateTime periodMonth)
    {
        var normalized = (value ?? string.Empty).Trim();
        var formats = new[]
        {
            "MM/yyyy",
            "M/yyyy",
            "MM-yyyy",
            "M-yyyy",
            "yyyy-MM",
            "yyyy/M",
            "yyyy/MM"
        };

        if (DateTime.TryParseExact(
            normalized,
            formats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed))
        {
            periodMonth = new DateTime(parsed.Year, parsed.Month, 1);
            return true;
        }

        periodMonth = default;
        return false;
    }


}

