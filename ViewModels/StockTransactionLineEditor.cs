using Accounting.Infrastructure;

namespace Accounting.ViewModels;

public sealed class StockTransactionLineEditor : ViewModelBase
{
    private int _lineNo;
    private long _itemId;
    private string _itemCode = string.Empty;
    private string _itemName = string.Empty;
    private string _uom = string.Empty;
    private decimal _qty;
    private decimal _unitCost;
    private string _expenseAccountCode = string.Empty;
    private string _expenseAccountName = string.Empty;
    private string _notes = string.Empty;

    public int LineNo
    {
        get => _lineNo;
        set => SetProperty(ref _lineNo, value);
    }

    public long ItemId
    {
        get => _itemId;
        set => SetProperty(ref _itemId, value);
    }

    public string ItemCode
    {
        get => _itemCode;
        set => SetProperty(ref _itemCode, value);
    }

    public string ItemName
    {
        get => _itemName;
        set => SetProperty(ref _itemName, value);
    }

    public string Uom
    {
        get => _uom;
        set => SetProperty(ref _uom, value);
    }

    public decimal Qty
    {
        get => _qty;
        set => SetProperty(ref _qty, value);
    }

    public decimal UnitCost
    {
        get => _unitCost;
        set => SetProperty(ref _unitCost, value);
    }

    public string ExpenseAccountCode
    {
        get => _expenseAccountCode;
        set => SetProperty(ref _expenseAccountCode, value);
    }

    public string ExpenseAccountName
    {
        get => _expenseAccountName;
        set => SetProperty(ref _expenseAccountName, value);
    }

    public string Notes
    {
        get => _notes;
        set => SetProperty(ref _notes, value);
    }
}
