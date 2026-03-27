namespace Accounting.Services;

public enum DashboardPeriodGranularity
{
    Daily = 0,
    Monthly = 1,
    Yearly = 2
}

public enum DashboardAlertSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2
}

public sealed class DashboardFilterOptions
{
    public List<ManagedCompany> Companies { get; init; } = new();

    public List<ManagedLocation> Locations { get; init; } = new();

    public long? DefaultCompanyId { get; init; }

    public long? DefaultLocationId { get; init; }

    public string BaseCurrencyCode { get; init; } = "IDR";
}

public sealed class AccountingDashboardRequest
{
    public long UserId { get; init; }

    public long RoleId { get; init; }

    public long CompanyId { get; init; }

    public long? LocationId { get; init; }

    public DateTime PeriodStart { get; init; } = DateTime.Today;

    public DashboardPeriodGranularity PeriodGranularity { get; init; } = DashboardPeriodGranularity.Monthly;

    public string CurrencyCode { get; init; } = "IDR";
}

public sealed class DashboardHeaderContext
{
    public string CompanyDisplayName { get; init; } = string.Empty;

    public string LocationDisplayName { get; init; } = string.Empty;

    public string CurrencyCode { get; init; } = "IDR";
}

public sealed class KpiMetricItem
{
    public string Key { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public string IconGlyph { get; init; } = string.Empty;

    public decimal PrimaryValue { get; init; }

    public decimal? SecondaryValue { get; init; }

    public decimal? PreviousValue { get; init; }

    public decimal? DeltaPercent { get; init; }

    public bool IsPositiveWhenUp { get; init; } = true;
}

public sealed class TrendPoint
{
    public string Label { get; init; } = string.Empty;

    public DateTime PeriodStart { get; init; }

    public decimal Revenue { get; init; }

    public decimal Expense { get; init; }
}

public sealed class ExpenseAccountBarItem
{
    public string AccountCode { get; init; } = string.Empty;

    public string AccountName { get; init; } = string.Empty;

    public decimal Amount { get; init; }
}

public sealed class CompositionSlice
{
    public string Label { get; init; } = string.Empty;

    public string GroupCode { get; init; } = string.Empty;

    public decimal Amount { get; init; }
}

public sealed class DashboardJournalItem
{
    public long JournalId { get; init; }

    public string JournalNo { get; init; } = string.Empty;

    public DateTime JournalDate { get; init; }

    public string ReferenceNo { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public decimal TotalAmount { get; init; }
}

public sealed class GlSnapshotData
{
    public int DraftCount { get; init; }

    public int PostedCount { get; init; }

    public int PendingPostingCount { get; init; }

    public List<DashboardJournalItem> RecentTransactions { get; init; } = new();
}

public sealed class CashBankBalanceItem
{
    public string AccountCode { get; init; } = string.Empty;

    public string AccountName { get; init; } = string.Empty;

    public decimal EndingBalance { get; init; }
}

public sealed class CashBankSnapshotData
{
    public decimal TotalBalance { get; init; }

    public decimal TodayInflow { get; init; }

    public decimal TodayOutflow { get; init; }

    public decimal PeriodInflow { get; init; }

    public decimal PeriodOutflow { get; init; }

    public List<CashBankBalanceItem> Accounts { get; init; } = new();
}

public sealed class InventoryMovementItem
{
    public string ItemCode { get; init; } = string.Empty;

    public string ItemName { get; init; } = string.Empty;

    public string Uom { get; init; } = string.Empty;

    public decimal Qty { get; init; }
}

public sealed class InventoryAgingBucket
{
    public string Label { get; init; } = string.Empty;

    public decimal Amount { get; init; }
}

public sealed class InventorySnapshotData
{
    public decimal TotalValue { get; init; }

    public int LowStockCount { get; init; }

    public List<InventoryMovementItem> TopMovingItems { get; init; } = new();

    public List<ManagedStockEntry> LowStockItems { get; init; } = new();

    public List<InventoryAgingBucket> AgingBuckets { get; init; } = new();
}

public sealed class DashboardAlertItem
{
    public string Key { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public string ActionLabel { get; init; } = string.Empty;

    public DashboardAlertSeverity Severity { get; init; } = DashboardAlertSeverity.Info;

    public int Count { get; init; }

    public DashboardDrillRequest? DrillRequest { get; init; }
}

public sealed class AccountingDashboardData
{
    public DashboardHeaderContext HeaderContext { get; init; } = new();

    public List<KpiMetricItem> Kpis { get; init; } = new();

    public List<TrendPoint> RevenueExpenseTrend { get; init; } = new();

    public List<ExpenseAccountBarItem> TopExpenseAccounts { get; init; } = new();

    public List<CompositionSlice> AssetComposition { get; init; } = new();

    public GlSnapshotData GlSnapshot { get; init; } = new();

    public CashBankSnapshotData CashBank { get; init; } = new();

    public InventorySnapshotData Inventory { get; init; } = new();

    public List<DashboardAlertItem> Alerts { get; init; } = new();

    public DateTime LastUpdatedAt { get; init; } = DateTime.Now;
}

public sealed class DashboardDrillRequest
{
    public string TargetModule { get; init; } = string.Empty;

    public string TargetSubCode { get; init; } = string.Empty;

    public long CompanyId { get; init; }

    public long? LocationId { get; init; }

    public DateTime PeriodStart { get; init; }

    public DashboardPeriodGranularity PeriodGranularity { get; init; } = DashboardPeriodGranularity.Monthly;

    public string AccountCode { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string Keyword { get; init; } = string.Empty;
}
