using Accounting.Services;

namespace Accounting.ViewModels;

public sealed partial class ReportsViewModel
{
    public void ApplyDashboardDrillDown(DashboardDrillRequest request)
    {
        if (request is null)
        {
            return;
        }

        PeriodMonth = request.PeriodStart == default
            ? new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1)
            : new DateTime(request.PeriodStart.Year, request.PeriodStart.Month, 1);

        SelectedGeneralLedgerAccountCode = request.AccountCode ?? string.Empty;
        GeneralLedgerKeyword = request.Keyword ?? string.Empty;
        NavigateToReportSubmenu(string.IsNullOrWhiteSpace(request.TargetSubCode) ? "trial_balance" : request.TargetSubCode);

        if (request.CompanyId > 0 &&
            request.LocationId.HasValue &&
            request.LocationId.Value > 0)
        {
            _dashboardCompanyId = request.CompanyId;
            _dashboardLocationId = request.LocationId.Value;
        }
        else if (!request.LocationId.HasValue)
        {
            StatusMessage = "Drill-down dashboard untuk filter semua lokasi belum didukung di workspace laporan. Menampilkan konteks aktif.";
        }

        _ = LoadReportsAsync();
    }
}
