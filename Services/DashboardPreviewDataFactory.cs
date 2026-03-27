namespace Accounting.Services;

public static class DashboardPreviewDataFactory
{
    public static AccountingDashboardData Create(AccountingDashboardRequest request, string companyDisplayName, string locationDisplayName)
    {
        var monthStart = request.PeriodStart == default
            ? new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1)
            : new DateTime(request.PeriodStart.Year, request.PeriodStart.Month, 1);

        return new AccountingDashboardData
        {
            HeaderContext = new DashboardHeaderContext
            {
                CompanyDisplayName = companyDisplayName,
                LocationDisplayName = locationDisplayName,
                CurrencyCode = string.IsNullOrWhiteSpace(request.CurrencyCode) ? "IDR" : request.CurrencyCode
            },
            Kpis = new List<KpiMetricItem>
            {
                new() { Key = "assets", Label = "Total Assets", IconGlyph = "\uE81E", PrimaryValue = 482500000m, PreviousValue = 455000000m, DeltaPercent = 6.04m, IsPositiveWhenUp = true },
                new() { Key = "liabilities", Label = "Total Liabilities", IconGlyph = "\uE8F1", PrimaryValue = 176400000m, PreviousValue = 182100000m, DeltaPercent = -3.13m, IsPositiveWhenUp = false },
                new() { Key = "equity", Label = "Equity", IconGlyph = "\uE8C7", PrimaryValue = 306100000m, PreviousValue = 272900000m, DeltaPercent = 12.16m, IsPositiveWhenUp = true },
                new() { Key = "revenue", Label = "Revenue", IconGlyph = "\uEAFD", PrimaryValue = 129500000m, SecondaryValue = 764800000m, PreviousValue = 117300000m, DeltaPercent = 10.40m, IsPositiveWhenUp = true },
                new() { Key = "expenses", Label = "Expenses", IconGlyph = "\uE91A", PrimaryValue = 84200000m, SecondaryValue = 486400000m, PreviousValue = 79800000m, DeltaPercent = 5.51m, IsPositiveWhenUp = false },
                new() { Key = "net_profit", Label = "Net Profit / Loss", IconGlyph = "\uE9D2", PrimaryValue = 45300000m, PreviousValue = 37500000m, DeltaPercent = 20.80m, IsPositiveWhenUp = true }
            },
            RevenueExpenseTrend = Enumerable.Range(0, 12).Select(offset =>
            {
                var current = monthStart.AddMonths(offset - 11);
                return new TrendPoint
                {
                    Label = current.ToString("MMM yy"),
                    PeriodStart = current,
                    Revenue = 98000000m + (offset * 2500000m),
                    Expense = 64000000m + (offset * 1800000m)
                };
            }).ToList(),
            TopExpenseAccounts =
            [
                new() { AccountCode = "6100", AccountName = "Biaya Gaji", Amount = 25400000m },
                new() { AccountCode = "6200", AccountName = "Biaya Distribusi", Amount = 19800000m },
                new() { AccountCode = "6300", AccountName = "Biaya Utilitas", Amount = 12600000m },
                new() { AccountCode = "6400", AccountName = "Biaya Administrasi", Amount = 9800000m },
                new() { AccountCode = "6500", AccountName = "Biaya Perawatan", Amount = 7600000m }
            ],
            AssetComposition =
            [
                new() { Label = "Current Asset", GroupCode = "CurrentAsset", Amount = 298000000m },
                new() { Label = "Fixed Asset", GroupCode = "FixedAsset", Amount = 184500000m }
            ],
            GlSnapshot = new GlSnapshotData
            {
                DraftCount = 4,
                PostedCount = 126,
                PendingPostingCount = 7,
                RecentTransactions =
                [
                    new() { JournalId = 101, JournalNo = "JV-202603-001", JournalDate = monthStart.AddDays(20), ReferenceNo = "AP-2301", Description = "Accrual utilities", Status = "POSTED", TotalAmount = 5600000m },
                    new() { JournalId = 102, JournalNo = "JV-202603-002", JournalDate = monthStart.AddDays(20), ReferenceNo = "AR-1902", Description = "Sales adjustment", Status = "APPROVED", TotalAmount = 4200000m },
                    new() { JournalId = 103, JournalNo = "JV-202603-003", JournalDate = monthStart.AddDays(19), ReferenceNo = "INV-1904", Description = "Inventory costing sync", Status = "DRAFT", TotalAmount = 14800000m }
                ]
            },
            CashBank = new CashBankSnapshotData
            {
                TotalBalance = 286400000m,
                TodayInflow = 18200000m,
                TodayOutflow = 12400000m,
                PeriodInflow = 241500000m,
                PeriodOutflow = 206300000m,
                Accounts =
                [
                    new() { AccountCode = "1101", AccountName = "Kas Operasional", EndingBalance = 82400000m },
                    new() { AccountCode = "1102", AccountName = "Bank BCA", EndingBalance = 131000000m },
                    new() { AccountCode = "1103", AccountName = "Bank Mandiri", EndingBalance = 73000000m }
                ]
            },
            Inventory = new InventorySnapshotData
            {
                TotalValue = 212700000m,
                LowStockCount = 3,
                TopMovingItems =
                [
                    new() { ItemCode = "FG-001", ItemName = "Produk A", Uom = "PCS", Qty = 1480m },
                    new() { ItemCode = "RM-014", ItemName = "Bahan Baku X", Uom = "KG", Qty = 920m },
                    new() { ItemCode = "FG-003", ItemName = "Produk C", Uom = "PCS", Qty = 870m }
                ],
                LowStockItems =
                [
                    new() { Id = 1, ItemId = 1, ItemCode = "RM-021", ItemName = "Bahan Baku Resin", Uom = "KG", Qty = 4m, LocationCode = "WH1", LocationName = "Gudang Utama" },
                    new() { Id = 2, ItemId = 2, ItemCode = "PK-002", ItemName = "Karton Medium", Uom = "PCS", Qty = 7m, LocationCode = "WH1", LocationName = "Gudang Utama" }
                ],
                AgingBuckets = []
            },
            Alerts =
            [
                new()
                {
                    Key = "unposted_journals",
                    Title = "Jurnal Belum Posting",
                    Message = "7 jurnal masih menunggu final posting.",
                    ActionLabel = "Buka jurnal",
                    Severity = DashboardAlertSeverity.Warning,
                    Count = 7,
                    DrillRequest = new DashboardDrillRequest { TargetModule = "transactions", TargetSubCode = "jurnal_belum_posting", CompanyId = request.CompanyId, LocationId = request.LocationId, PeriodStart = monthStart, PeriodGranularity = request.PeriodGranularity }
                },
                new()
                {
                    Key = "low_stock",
                    Title = "Low Stock Alert",
                    Message = "3 item membutuhkan replenishment minggu ini.",
                    ActionLabel = "Buka alert stok",
                    Severity = DashboardAlertSeverity.Warning,
                    Count = 3,
                    DrillRequest = new DashboardDrillRequest { TargetModule = "inventory", TargetSubCode = "low_stock", CompanyId = request.CompanyId, LocationId = request.LocationId, PeriodStart = monthStart, PeriodGranularity = request.PeriodGranularity }
                }
            ],
            LastUpdatedAt = DateTime.Now
        };
    }
}
