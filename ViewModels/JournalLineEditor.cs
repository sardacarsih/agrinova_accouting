using Accounting.Infrastructure;
using Accounting.Services;

namespace Accounting.ViewModels;

public sealed class JournalLineEditor : ViewModelBase
{
    private int _lineNo;
    private string _accountCode = string.Empty;
    private string _accountName = string.Empty;
    private string _description = string.Empty;
    private decimal _debit;
    private decimal _credit;
    private string _departmentCode = string.Empty;
    private string _projectCode = string.Empty;
    private string _subledgerType = string.Empty;
    private long? _subledgerId;
    private string _subledgerCode = string.Empty;
    private string _subledgerName = string.Empty;
    private long? _costCenterId;
    private long? _blockId;
    private string _costCenterCode = string.Empty;
    private bool _hasValidationError;
    private string _validationMessage = string.Empty;

    public int LineNo
    {
        get => _lineNo;
        set => SetProperty(ref _lineNo, value);
    }

    public string AccountCode
    {
        get => _accountCode;
        set => SetProperty(ref _accountCode, value);
    }

    public string AccountName
    {
        get => _accountName;
        set => SetProperty(ref _accountName, value);
    }

    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    public decimal Debit
    {
        get => _debit;
        set => SetProperty(ref _debit, value);
    }

    public decimal Credit
    {
        get => _credit;
        set => SetProperty(ref _credit, value);
    }

    public string DepartmentCode
    {
        get => _departmentCode;
        set => SetProperty(ref _departmentCode, value);
    }

    public string ProjectCode
    {
        get => _projectCode;
        set => SetProperty(ref _projectCode, value);
    }

    public string SubledgerType
    {
        get => _subledgerType;
        set
        {
            if (SetProperty(ref _subledgerType, value))
            {
                OnPropertyChanged(nameof(SubledgerDisplay));
            }
        }
    }

    public long? SubledgerId
    {
        get => _subledgerId;
        set => SetProperty(ref _subledgerId, value);
    }

    public string SubledgerCode
    {
        get => _subledgerCode;
        set
        {
            if (SetProperty(ref _subledgerCode, value))
            {
                OnPropertyChanged(nameof(SubledgerDisplay));
            }
        }
    }

    public string SubledgerName
    {
        get => _subledgerName;
        set
        {
            if (SetProperty(ref _subledgerName, value))
            {
                OnPropertyChanged(nameof(SubledgerDisplay));
            }
        }
    }

    public string SubledgerDisplay =>
        string.IsNullOrWhiteSpace(SubledgerCode)
            ? string.Empty
            : string.IsNullOrWhiteSpace(SubledgerName)
                ? SubledgerCode
                : $"{SubledgerCode} - {SubledgerName}";

    public long? CostCenterId
    {
        get => _costCenterId;
        set => SetProperty(ref _costCenterId, value);
    }

    public long? BlockId
    {
        get => _blockId;
        set => SetProperty(ref _blockId, value);
    }

    public string CostCenterCode
    {
        get => _costCenterCode;
        set => SetProperty(ref _costCenterCode, value);
    }

    public bool HasValidationError
    {
        get => _hasValidationError;
        set => SetProperty(ref _hasValidationError, value);
    }

    public string ValidationMessage
    {
        get => _validationMessage;
        set => SetProperty(ref _validationMessage, value);
    }

    public ManagedJournalLine ToManaged()
    {
        return new ManagedJournalLine
        {
            LineNo = LineNo,
            AccountCode = AccountCode,
            AccountName = AccountName,
            Description = Description,
            Debit = Debit,
            Credit = Credit,
            DepartmentCode = DepartmentCode,
            ProjectCode = ProjectCode,
            SubledgerType = SubledgerType,
            SubledgerId = SubledgerId,
            SubledgerCode = SubledgerCode,
            SubledgerName = SubledgerName,
            CostCenterId = CostCenterId,
            BlockId = BlockId,
            CostCenterCode = CostCenterCode
        };
    }

    public static JournalLineEditor FromManaged(ManagedJournalLine source)
    {
        return new JournalLineEditor
        {
            LineNo = source.LineNo,
            AccountCode = source.AccountCode,
            AccountName = source.AccountName,
            Description = source.Description,
            Debit = source.Debit,
            Credit = source.Credit,
            DepartmentCode = source.DepartmentCode,
            ProjectCode = source.ProjectCode,
            SubledgerType = source.SubledgerType,
            SubledgerId = source.SubledgerId,
            SubledgerCode = source.SubledgerCode,
            SubledgerName = source.SubledgerName,
            CostCenterId = source.CostCenterId,
            BlockId = source.BlockId,
            CostCenterCode = source.CostCenterCode
        };
    }
}
