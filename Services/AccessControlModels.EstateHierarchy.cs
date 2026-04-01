namespace Accounting.Services;

public sealed class ManagedEstate
{
    public long Id { get; set; }

    public long CompanyId { get; set; }

    public long LocationId { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public List<ManagedDivision> Divisions { get; init; } = new();

    public override string ToString()
    {
        return string.IsNullOrWhiteSpace(Name)
            ? Code
            : $"{Code} - {Name}";
    }
}

public sealed class ManagedDivision
{
    public long Id { get; set; }

    public long EstateId { get; set; }

    public long CompanyId { get; set; }

    public long LocationId { get; set; }

    public string EstateCode { get; set; } = string.Empty;

    public string EstateName { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public List<ManagedBlock> Blocks { get; init; } = new();

    public override string ToString()
    {
        return string.IsNullOrWhiteSpace(Name)
            ? $"{EstateCode}-{Code}"
            : $"{Code} - {Name}";
    }
}

public sealed class ManagedBlock
{
    public long Id { get; set; }

    public long EstateId { get; set; }

    public long DivisionId { get; set; }

    public long CompanyId { get; set; }

    public long LocationId { get; set; }

    public string EstateCode { get; set; } = string.Empty;

    public string EstateName { get; set; } = string.Empty;

    public string DivisionCode { get; set; } = string.Empty;

    public string DivisionName { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public string CostCenterCode =>
        string.Join(
            "-",
            new[] { EstateCode, DivisionCode, Code }
                .Where(value => !string.IsNullOrWhiteSpace(value)));

    public override string ToString()
    {
        return string.IsNullOrWhiteSpace(Name)
            ? CostCenterCode
            : $"{Code} - {Name}";
    }
}

public sealed class EstateHierarchyWorkspace
{
    public List<ManagedEstate> Estates { get; init; } = new();
}

public sealed class EstateImportRow
{
    public int RowNumber { get; init; }

    public string Code { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public bool IsActive { get; init; } = true;
}

public sealed class DivisionImportRow
{
    public int RowNumber { get; init; }

    public string EstateCode { get; init; } = string.Empty;

    public string Code { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public bool IsActive { get; init; } = true;
}

public sealed class BlockImportRow
{
    public int RowNumber { get; init; }

    public string EstateCode { get; init; } = string.Empty;

    public string DivisionCode { get; init; } = string.Empty;

    public string Code { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public bool IsActive { get; init; } = true;
}

public sealed class EstateHierarchyImportBundle
{
    public List<EstateImportRow> Estates { get; init; } = new();

    public List<DivisionImportRow> Divisions { get; init; } = new();

    public List<BlockImportRow> Blocks { get; init; } = new();
}

public sealed class EstateHierarchyImportParseResult
{
    public bool IsSuccess { get; init; }

    public string Message { get; init; } = string.Empty;

    public EstateHierarchyImportBundle Bundle { get; init; } = new();

    public List<InventoryImportError> Errors { get; init; } = new();
}

public sealed class EstateHierarchyImportExecutionResult
{
    public bool IsSuccess { get; init; }

    public string Message { get; init; } = string.Empty;

    public int ImportedEstateCount { get; init; }

    public int ImportedDivisionCount { get; init; }

    public int ImportedBlockCount { get; init; }

    public List<InventoryImportError> Errors { get; init; } = new();
}
