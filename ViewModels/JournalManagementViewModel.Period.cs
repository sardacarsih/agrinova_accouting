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
        foreach (var period in periods)
        {
            var key = GetPeriodKey(period.PeriodMonth);
            _periodOpenByMonthKey[key] = period.IsOpen;
        }
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


}
