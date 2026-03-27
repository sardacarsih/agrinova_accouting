namespace Accounting.Services;

public sealed class InventoryImportCategoryRow
{
    public int RowNumber { get; init; }

    public string Code { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string AccountCode { get; init; } = string.Empty;

    public bool IsActive { get; init; } = true;
}

public sealed class InventoryImportItemRow
{
    public int RowNumber { get; init; }

    public string Code { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Uom { get; init; } = "PCS";

    public string CategoryCode { get; init; } = string.Empty;

    public bool IsActive { get; init; } = true;
}

public sealed class InventoryImportError
{
    public string SheetName { get; init; } = string.Empty;

    public int RowNumber { get; init; }

    public string Message { get; init; } = string.Empty;
}

public sealed class InventoryImportBundle
{
    public List<InventoryImportCategoryRow> Categories { get; init; } = new();

    public List<InventoryImportItemRow> Items { get; init; } = new();
}

public sealed class InventoryImportParseResult
{
    public bool IsSuccess { get; init; }

    public string Message { get; init; } = string.Empty;

    public InventoryImportBundle Bundle { get; init; } = new();

    public List<InventoryImportError> Errors { get; init; } = new();
}

public sealed class InventoryImportExecutionResult
{
    public bool IsSuccess { get; init; }

    public string Message { get; init; } = string.Empty;

    public int ImportedCategoryCount { get; init; }

    public int ImportedItemCount { get; init; }

    public List<InventoryImportError> Errors { get; init; } = new();
}

public sealed class InventoryOpeningBalanceRow
{
    public int RowNumber { get; init; }

    public string CompanyCode { get; init; } = string.Empty;

    public string LocationCode { get; init; } = string.Empty;

    public string WarehouseCode { get; init; } = string.Empty;

    public string ItemCode { get; init; } = string.Empty;

    public decimal Qty { get; init; }

    public decimal UnitCost { get; init; }

    public DateTime CutoffDate { get; init; } = DateTime.Today;

    public string ReferenceNo { get; init; } = string.Empty;

    public string Notes { get; init; } = string.Empty;
}

public sealed class InventoryOpeningBalanceBundle
{
    public List<InventoryOpeningBalanceRow> Rows { get; init; } = new();
}

public sealed class InventoryOpeningBalanceParseResult
{
    public bool IsSuccess { get; init; }

    public string Message { get; init; } = string.Empty;

    public InventoryOpeningBalanceBundle Bundle { get; init; } = new();

    public List<InventoryImportError> Errors { get; init; } = new();
}

public sealed class InventoryOpeningBalanceExecutionResult
{
    public bool IsSuccess { get; init; }

    public bool IsValidationOnly { get; init; }

    public string Message { get; init; } = string.Empty;

    public int ValidRowCount { get; init; }

    public int TransactionCount { get; init; }

    public int ImportedLineCount { get; init; }

    public decimal TotalQty { get; init; }

    public decimal TotalValue { get; init; }

    public List<InventoryImportError> Errors { get; init; } = new();
}

public sealed class InventoryCentralSyncSettings
{
    public string BaseUrl { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string UploadPath { get; set; } = "/api/inventory/sync/upload";

    public string DownloadPath { get; set; } = "/api/inventory/sync/download";

    public int TimeoutSeconds { get; set; } = 30;
}

public sealed class ManagedInventorySyncRun
{
    public long Id { get; set; }

    public long CompanyId { get; set; }

    public string Direction { get; set; } = string.Empty;

    public string TriggerMode { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string ActorUsername { get; set; } = string.Empty;

    public DateTime StartedAt { get; set; }

    public DateTime? EndedAt { get; set; }

    public DateTime? WatermarkFromUtc { get; set; }

    public DateTime? WatermarkToUtc { get; set; }

    public int TotalItems { get; set; }

    public int SuccessItems { get; set; }

    public int FailedItems { get; set; }

    public string Message { get; set; } = string.Empty;
}

public sealed class ManagedInventorySyncItemLog
{
    public long Id { get; set; }

    public long SyncRunId { get; set; }

    public long CompanyId { get; set; }

    public string Direction { get; set; } = string.Empty;

    public string ItemCode { get; set; } = string.Empty;

    public string CategoryCode { get; set; } = string.Empty;

    public string Operation { get; set; } = string.Empty;

    public string Result { get; set; } = string.Empty;

    public string ErrorMessage { get; set; } = string.Empty;

    public DateTime LoggedAt { get; set; }
}
