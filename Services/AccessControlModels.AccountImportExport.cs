namespace Accounting.Services;

public sealed class AccountImportRow
{
    public int RowNumber { get; init; }

    public string Code { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string AccountType { get; init; } = string.Empty;

    public string ParentAccountCode { get; init; } = string.Empty;

    public bool IsActive { get; init; } = true;

    public bool RequiresDepartment { get; init; }

    public bool RequiresProject { get; init; }

    public bool RequiresCostCenter { get; init; }

    public bool RequiresSubledger { get; init; }

    public string AllowedSubledgerType { get; init; } = string.Empty;
}

public sealed class AccountImportBundle
{
    public List<AccountImportRow> Accounts { get; init; } = new();
}

public sealed class AccountImportParseResult
{
    public bool IsSuccess { get; init; }

    public string Message { get; init; } = string.Empty;

    public AccountImportBundle Bundle { get; init; } = new();

    public List<InventoryImportError> Errors { get; init; } = new();
}

public sealed class AccountImportExecutionResult
{
    public bool IsSuccess { get; init; }

    public string Message { get; init; } = string.Empty;

    public int CreatedCount { get; init; }

    public int UpdatedCount { get; init; }

    public List<InventoryImportError> Errors { get; init; } = new();
}
