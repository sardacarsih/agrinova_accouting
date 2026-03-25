namespace Accounting.Services;

public sealed class ManagedInventoryCategory
{
    public long Id { get; set; }

    public long CompanyId { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string AccountCode { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    public override string ToString()
    {
        return string.IsNullOrWhiteSpace(Name) ? Code : Name;
    }
}

public sealed class ManagedInventoryItem
{
    public long Id { get; set; }

    public long CompanyId { get; set; }

    public long? CategoryId { get; set; }

    public string CategoryName { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Uom { get; set; } = "PCS";

    public string Category { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    public override string ToString()
    {
        if (string.IsNullOrWhiteSpace(Code))
        {
            return Name;
        }

        return string.IsNullOrWhiteSpace(Name) ? Code : $"{Code} - {Name}";
    }
}

public sealed class ManagedStockEntry
{
    public long Id { get; set; }

    public long ItemId { get; set; }

    public string ItemCode { get; set; } = string.Empty;

    public string ItemName { get; set; } = string.Empty;

    public string Uom { get; set; } = string.Empty;

    public string LocationCode { get; set; } = string.Empty;

    public string LocationName { get; set; } = string.Empty;

    public decimal Qty { get; set; }
}

public sealed class ManagedInventoryUnit
{
    public long Id { get; set; }

    public long CompanyId { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    public override string ToString()
    {
        if (string.IsNullOrWhiteSpace(Code))
        {
            return Name;
        }

        return string.IsNullOrWhiteSpace(Name) ? Code : $"{Code} - {Name}";
    }
}

public sealed class ManagedWarehouse
{
    public long Id { get; set; }

    public long CompanyId { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public long? LocationId { get; set; }

    public string LocationName { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    public override string ToString()
    {
        if (string.IsNullOrWhiteSpace(Code))
        {
            return Name;
        }

        return string.IsNullOrWhiteSpace(Name) ? Code : $"{Code} - {Name}";
    }
}

public sealed class ManagedStockTransaction
{
    public long Id { get; set; }

    public long CompanyId { get; set; }

    public long LocationId { get; set; }

    public string TransactionNo { get; set; } = string.Empty;

    public string TransactionType { get; set; } = "STOCK_IN";

    public DateTime TransactionDate { get; set; } = DateTime.Today;

    public long? WarehouseId { get; set; }

    public string WarehouseName { get; set; } = string.Empty;

    public long? DestinationWarehouseId { get; set; }

    public string DestinationWarehouseName { get; set; } = string.Empty;

    public string ReferenceNo { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Status { get; set; } = "DRAFT";
}

public sealed class ManagedStockTransactionLine
{
    public long Id { get; set; }

    public long TransactionId { get; set; }

    public int LineNo { get; set; }

    public long ItemId { get; set; }

    public string ItemCode { get; set; } = string.Empty;

    public string ItemName { get; set; } = string.Empty;

    public string Uom { get; set; } = string.Empty;

    public decimal Qty { get; set; }

    public decimal UnitCost { get; set; }

    public string ExpenseAccountCode { get; set; } = string.Empty;

    public string Notes { get; set; } = string.Empty;
}

public sealed class ManagedStockTransactionSummary
{
    public long Id { get; set; }

    public string TransactionNo { get; set; } = string.Empty;

    public string TransactionType { get; set; } = string.Empty;

    public DateTime TransactionDate { get; set; }

    public string WarehouseName { get; set; } = string.Empty;

    public string ReferenceNo { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public decimal TotalQty { get; set; }
}

public sealed class StockTransactionBundle
{
    public ManagedStockTransaction Header { get; init; } = new();

    public List<ManagedStockTransactionLine> Lines { get; init; } = new();
}

public sealed class StockOpnameBundle
{
    public ManagedStockOpname Header { get; init; } = new();

    public List<ManagedStockOpnameLine> Lines { get; init; } = new();
}

public sealed class ManagedStockOpname
{
    public long Id { get; set; }

    public long CompanyId { get; set; }

    public long LocationId { get; set; }

    public string OpnameNo { get; set; } = string.Empty;

    public DateTime OpnameDate { get; set; } = DateTime.Today;

    public long? WarehouseId { get; set; }

    public string WarehouseName { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Status { get; set; } = "DRAFT";
}

public sealed class ManagedStockOpnameLine
{
    public long Id { get; set; }

    public long OpnameId { get; set; }

    public int LineNo { get; set; }

    public long ItemId { get; set; }

    public string ItemCode { get; set; } = string.Empty;

    public string ItemName { get; set; } = string.Empty;

    public string Uom { get; set; } = string.Empty;

    public decimal SystemQty { get; set; }

    public decimal ActualQty { get; set; }

    public decimal DifferenceQty { get; set; }

    public string Notes { get; set; } = string.Empty;
}

public sealed class InventoryDashboardData
{
    public int TotalItemCount { get; init; }

    public decimal TotalStockValue { get; init; }

    public int LowStockCount { get; init; }

    public int PendingTransactionCount { get; init; }

    public List<ManagedStockTransactionSummary> RecentTransactions { get; init; } = new();

    public List<ManagedStockEntry> LowStockItems { get; init; } = new();
}

public sealed class StockMovementReportRow
{
    public string ItemCode { get; set; } = string.Empty;

    public string ItemName { get; set; } = string.Empty;

    public string Uom { get; set; } = string.Empty;

    public decimal OpeningQty { get; set; }

    public decimal InQty { get; set; }

    public decimal OutQty { get; set; }

    public decimal AdjustmentQty { get; set; }

    public decimal ClosingQty { get; set; }
}

public sealed class StockValuationRow
{
    public string ItemCode { get; set; } = string.Empty;

    public string ItemName { get; set; } = string.Empty;

    public string Uom { get; set; } = string.Empty;

    public decimal Qty { get; set; }

    public decimal AvgCost { get; set; }

    public decimal TotalValue { get; set; }
}

public sealed class InventoryOutboundCompareRow
{
    public DateTime TxDate { get; set; }

    public string ItemCode { get; set; } = string.Empty;

    public string ItemName { get; set; } = string.Empty;

    public string WarehouseCode { get; set; } = string.Empty;

    public string WarehouseName { get; set; } = string.Empty;

    public decimal QtyLkOracle { get; set; }

    public decimal QtyTransferInternal { get; set; }

    public decimal QtyDiff { get; set; }

    public string MatchStatus { get; set; } = string.Empty;
}

public sealed class InventoryTransactionSearchFilter
{
    public DateTime? DateFrom { get; set; }

    public DateTime? DateTo { get; set; }

    public string Keyword { get; set; } = string.Empty;

    public string? Status { get; set; }

    public string? TransactionType { get; set; }
}

public sealed class InventoryItemSearchFilter
{
    public string Keyword { get; set; } = string.Empty;

    public int Page { get; set; } = 1;

    public int PageSize { get; set; } = 50;
}

public sealed class InventoryItemSearchResult
{
    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 50;

    public int TotalCount { get; init; }

    public List<ManagedInventoryItem> Items { get; init; } = new();
}

public sealed class InventoryWorkspaceData
{
    public long? MasterCompanyId { get; set; }

    public string MasterCompanyCode { get; set; } = string.Empty;

    public string MasterCompanyName { get; set; } = string.Empty;

    public bool CanMaintainMasterInventoryData { get; set; }

    public List<ManagedInventoryCategory> Categories { get; init; } = new();

    public List<ManagedInventoryItem> Items { get; init; } = new();

    public List<ManagedStockEntry> StockEntries { get; init; } = new();

    public List<ManagedAccount> Accounts { get; init; } = new();

    public List<ManagedInventoryUnit> Units { get; init; } = new();

    public List<ManagedWarehouse> Warehouses { get; init; } = new();
}

public sealed class InventoryCostingSettings
{
    public long CompanyId { get; set; }

    public string ValuationMethod { get; set; } = "AVERAGE";

    public string CogsAccountCode { get; set; } = string.Empty;
}

public sealed class InventoryLocationCostingSettings
{
    public long CompanyId { get; set; }

    public long LocationId { get; set; }

    public bool UseCompanyDefault { get; set; } = true;

    public string ValuationMethod { get; set; } = "AVERAGE";

    public string CogsAccountCode { get; set; } = string.Empty;
}
