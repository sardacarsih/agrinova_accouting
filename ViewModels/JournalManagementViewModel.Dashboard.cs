using Accounting.Services;

namespace Accounting.ViewModels;

public sealed partial class JournalManagementViewModel
{
    public void ApplyDashboardDrillDown(DashboardDrillRequest request)
    {
        if (request is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(request.TargetSubCode))
        {
            NavigateToJournalScenario(request.TargetSubCode);
        }

        if (request.CompanyId > 0 &&
            request.LocationId.HasValue &&
            request.LocationId.Value > 0)
        {
            _dashboardSearchCompanyId = request.CompanyId;
            _dashboardSearchLocationId = request.LocationId.Value;
        }
        else if (!request.LocationId.HasValue)
        {
            StatusMessage = "Drill-down dashboard untuk filter semua lokasi belum didukung di workspace jurnal. Menampilkan konteks aktif.";
        }

        SearchPeriodMonth = request.PeriodStart == default
            ? new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1)
            : new DateTime(request.PeriodStart.Year, request.PeriodStart.Month, 1);
        SearchDateFrom = null;
        SearchDateTo = null;
        SearchKeyword = request.Keyword ?? string.Empty;
        SearchStatus = request.Status ?? string.Empty;
        _ = SearchAsync();
    }
}
