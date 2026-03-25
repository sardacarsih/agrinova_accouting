namespace Accounting.ViewModels;

public sealed class JournalExportPreviewLine
{
    public long JournalId { get; init; }

    public string JournalNo { get; init; } = string.Empty;

    public DateTime JournalDate { get; init; }

    public string ReferenceNo { get; init; } = string.Empty;

    public string JournalStatus { get; init; } = string.Empty;

    public int LineNo { get; init; }

    public string AccountCode { get; init; } = string.Empty;

    public string AccountName { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public decimal Debit { get; init; }

    public decimal Credit { get; init; }

    public string DepartmentCode { get; init; } = string.Empty;

    public string ProjectCode { get; init; } = string.Empty;

    public string CostCenterCode { get; init; } = string.Empty;
}
