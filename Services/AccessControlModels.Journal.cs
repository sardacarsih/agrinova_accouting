namespace Accounting.Services;

public sealed class ManagedAccount
{
    public long Id { get; set; }

    public long CompanyId { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string AccountType { get; set; } = "ASSET";

    public long? ParentAccountId { get; set; }

    public string ParentAccountCode { get; set; } = string.Empty;

    public int HierarchyLevel { get; set; } = 1;

    public bool IsPosting { get; set; } = true;

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

public sealed class AccountSearchFilter
{
    public string Keyword { get; set; } = string.Empty;

    public string Status { get; set; } = "Aktif";

    public int Page { get; set; } = 1;

    public int PageSize { get; set; } = 50;
}

public sealed class AccountSearchResult
{
    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 50;

    public int TotalCount { get; init; }

    public List<ManagedAccount> Items { get; init; } = new();
}

public sealed class ManagedAccountingPeriod
{
    public long Id { get; set; }

    public long CompanyId { get; set; }

    public long LocationId { get; set; }

    public DateTime PeriodMonth { get; set; }

    public bool IsOpen { get; set; }

    public DateTime? ClosedAt { get; set; }

    public string ClosedBy { get; set; } = string.Empty;

    public string Note { get; set; } = string.Empty;
}

public sealed class ManagedJournalHeader
{
    public long Id { get; set; }

    public long CompanyId { get; set; }

    public long LocationId { get; set; }

    public string JournalNo { get; set; } = string.Empty;

    public DateTime JournalDate { get; set; } = DateTime.Today;

    public DateTime PeriodMonth { get; set; }

    public string ReferenceNo { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Status { get; set; } = "DRAFT";
}

public sealed class ManagedJournalLine
{
    public int LineNo { get; set; }

    public string AccountCode { get; set; } = string.Empty;

    public string AccountName { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public decimal Debit { get; set; }

    public decimal Credit { get; set; }

    public string DepartmentCode { get; set; } = string.Empty;

    public string ProjectCode { get; set; } = string.Empty;

    public string CostCenterCode { get; set; } = string.Empty;
}

public sealed class ManagedJournalSummary
{
    public long Id { get; set; }

    public string JournalNo { get; set; } = string.Empty;

    public DateTime JournalDate { get; set; }

    public string ReferenceNo { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Status { get; set; } = "DRAFT";

    public decimal TotalDebit { get; set; }

    public decimal TotalCredit { get; set; }
}

public sealed class ManagedTrialBalanceRow
{
    public string AccountCode { get; set; } = string.Empty;

    public string AccountName { get; set; } = string.Empty;

    public decimal TotalDebit { get; set; }

    public decimal TotalCredit { get; set; }

    public decimal NetBalance => TotalDebit - TotalCredit;
}

public sealed class ManagedProfitLossRow
{
    public string Section { get; set; } = string.Empty;

    public string AccountCode { get; set; } = string.Empty;

    public string AccountName { get; set; } = string.Empty;

    public decimal Amount { get; set; }
}

public sealed class ManagedBalanceSheetRow
{
    public string Section { get; set; } = string.Empty;

    public string AccountCode { get; set; } = string.Empty;

    public string ParentAccountCode { get; set; } = string.Empty;

    public string AccountName { get; set; } = string.Empty;

    public int Level { get; set; } = 1;

    public bool HasChildren { get; set; }

    public bool IsPosting { get; set; } = true;

    public decimal Amount { get; set; }
}

public sealed class ManagedGeneralLedgerRow
{
    public DateTime JournalDate { get; set; }

    public string JournalNo { get; set; } = string.Empty;

    public string ReferenceNo { get; set; } = string.Empty;

    public string JournalDescription { get; set; } = string.Empty;

    public string AccountCode { get; set; } = string.Empty;

    public string AccountName { get; set; } = string.Empty;

    public string LineDescription { get; set; } = string.Empty;

    public decimal Debit { get; set; }

    public decimal Credit { get; set; }

    public decimal RunningBalance { get; set; }
}

public sealed class ManagedSubLedgerRow
{
    public DateTime JournalDate { get; set; }

    public string JournalNo { get; set; } = string.Empty;

    public string ReferenceNo { get; set; } = string.Empty;

    public string JournalDescription { get; set; } = string.Empty;

    public string AccountCode { get; set; } = string.Empty;

    public string AccountName { get; set; } = string.Empty;

    public string DepartmentCode { get; set; } = string.Empty;

    public string ProjectCode { get; set; } = string.Empty;

    public string CostCenterCode { get; set; } = string.Empty;

    public string LineDescription { get; set; } = string.Empty;

    public decimal Debit { get; set; }

    public decimal Credit { get; set; }

    public decimal RunningBalance { get; set; }
}

public sealed class ManagedCashFlowRow
{
    public string AccountCode { get; set; } = string.Empty;

    public string AccountName { get; set; } = string.Empty;

    public decimal OpeningBalance { get; set; }

    public decimal CashIn { get; set; }

    public decimal CashOut { get; set; }

    public decimal EndingBalance { get; set; }
}

public sealed class ManagedAccountMutationRow
{
    public string AccountCode { get; set; } = string.Empty;

    public string AccountName { get; set; } = string.Empty;

    public decimal OpeningBalance { get; set; }

    public decimal MutationDebit { get; set; }

    public decimal MutationCredit { get; set; }

    public decimal EndingBalance { get; set; }
}

public sealed class ManagedJournalBundle
{
    public ManagedJournalHeader Header { get; init; } = new();

    public List<ManagedJournalLine> Lines { get; init; } = new();
}

public sealed class JournalWorkspaceData
{
    public List<ManagedAccount> Accounts { get; init; } = new();

    public List<ManagedJournalSummary> Journals { get; init; } = new();
}

public sealed class JournalSearchFilter
{
    public DateTime? PeriodMonth { get; init; }

    public DateTime? DateFrom { get; init; }

    public DateTime? DateTo { get; init; }

    public string Keyword { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;
}

public sealed class JournalImportPreviewItem
{
    public int RowNumber { get; init; }

    public string JournalNo { get; init; } = string.Empty;

    public string AccountCode { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public decimal Debit { get; init; }

    public decimal Credit { get; init; }

    public string DepartmentCode { get; init; } = string.Empty;

    public string ProjectCode { get; init; } = string.Empty;

    public string CostCenterCode { get; init; } = string.Empty;

    public bool IsValid { get; init; }

    public string ValidationMessage { get; init; } = string.Empty;
}

public sealed class JournalImportBundleResult
{
    public ManagedJournalHeader Header { get; init; } = new();

    public List<ManagedJournalLine> Lines { get; init; } = new();

    public bool IsValid { get; init; }

    public string ValidationMessage { get; init; } = string.Empty;
}

public sealed class JournalImportLoadResult
{
    public bool IsSuccess { get; init; }

    public string Message { get; init; } = string.Empty;

    public List<JournalImportBundleResult> JournalBundles { get; init; } = new();

    public List<JournalImportPreviewItem> PreviewItems { get; init; } = new();
}
