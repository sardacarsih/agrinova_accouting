using System.Collections.ObjectModel;
using Accounting.Infrastructure;
using Accounting.Services;

namespace Accounting.ViewModels;

public sealed class InventoryLookupOption
{
    public string Code { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public object? Value { get; init; }
}

public sealed class StockTransactionLineEditor : ViewModelBase
{
    private int _lineNo;
    private long _itemId;
    private string _itemCode = string.Empty;
    private string _itemName = string.Empty;
    private string _uom = string.Empty;
    private decimal _qty;
    private decimal _unitCost;
    private long? _warehouseId;
    private string _warehouseName = string.Empty;
    private long? _destinationWarehouseId;
    private string _destinationWarehouseName = string.Empty;
    private string _expenseAccountCode = string.Empty;
    private string _expenseAccountName = string.Empty;
    private string _notes = string.Empty;
    private string _itemLookupText = string.Empty;
    private string _warehouseLookupText = string.Empty;
    private string _destinationWarehouseLookupText = string.Empty;
    private string _expenseAccountLookupText = string.Empty;
    private bool _isItemLookupPopupOpen;
    private bool _isWarehouseLookupPopupOpen;
    private bool _isDestinationWarehouseLookupPopupOpen;
    private bool _isExpenseAccountLookupPopupOpen;
    private InventoryLookupOption? _selectedItemLookupSuggestion;
    private InventoryLookupOption? _selectedWarehouseLookupSuggestion;
    private InventoryLookupOption? _selectedDestinationWarehouseLookupSuggestion;
    private InventoryLookupOption? _selectedExpenseAccountLookupSuggestion;

    public StockTransactionLineEditor()
    {
        ItemLookupSuggestions = new ObservableCollection<InventoryLookupOption>();
        WarehouseLookupSuggestions = new ObservableCollection<InventoryLookupOption>();
        DestinationWarehouseLookupSuggestions = new ObservableCollection<InventoryLookupOption>();
        ExpenseAccountLookupSuggestions = new ObservableCollection<InventoryLookupOption>();
    }

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

    public long? WarehouseId
    {
        get => _warehouseId;
        set => SetProperty(ref _warehouseId, value);
    }

    public string WarehouseName
    {
        get => _warehouseName;
        set => SetProperty(ref _warehouseName, value);
    }

    public long? DestinationWarehouseId
    {
        get => _destinationWarehouseId;
        set => SetProperty(ref _destinationWarehouseId, value);
    }

    public string DestinationWarehouseName
    {
        get => _destinationWarehouseName;
        set => SetProperty(ref _destinationWarehouseName, value);
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

    public string ItemLookupText
    {
        get => _itemLookupText;
        set
        {
            if (!SetProperty(ref _itemLookupText, value))
            {
                return;
            }

            if (!string.Equals((_itemLookupText ?? string.Empty).Trim(), (_itemCode ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
            {
                ClearResolvedItemState();
            }
        }
    }

    public string ExpenseAccountLookupText
    {
        get => _expenseAccountLookupText;
        set
        {
            if (!SetProperty(ref _expenseAccountLookupText, value))
            {
                return;
            }

            if (!string.Equals((_expenseAccountLookupText ?? string.Empty).Trim(), (_expenseAccountCode ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
            {
                ClearResolvedExpenseAccountState();
            }
        }
    }

    public string WarehouseLookupText
    {
        get => _warehouseLookupText;
        set
        {
            if (!SetProperty(ref _warehouseLookupText, value))
            {
                return;
            }

            if (!string.Equals((_warehouseLookupText ?? string.Empty).Trim(), (_warehouseName ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
            {
                ClearResolvedWarehouseState();
            }
        }
    }

    public string DestinationWarehouseLookupText
    {
        get => _destinationWarehouseLookupText;
        set
        {
            if (!SetProperty(ref _destinationWarehouseLookupText, value))
            {
                return;
            }

            if (!string.Equals((_destinationWarehouseLookupText ?? string.Empty).Trim(), (_destinationWarehouseName ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
            {
                ClearResolvedDestinationWarehouseState();
            }
        }
    }

    public ObservableCollection<InventoryLookupOption> ItemLookupSuggestions { get; }

    public ObservableCollection<InventoryLookupOption> WarehouseLookupSuggestions { get; }

    public ObservableCollection<InventoryLookupOption> DestinationWarehouseLookupSuggestions { get; }

    public ObservableCollection<InventoryLookupOption> ExpenseAccountLookupSuggestions { get; }

    public bool IsItemLookupPopupOpen
    {
        get => _isItemLookupPopupOpen;
        set => SetProperty(ref _isItemLookupPopupOpen, value);
    }

    public bool IsExpenseAccountLookupPopupOpen
    {
        get => _isExpenseAccountLookupPopupOpen;
        set => SetProperty(ref _isExpenseAccountLookupPopupOpen, value);
    }

    public bool IsWarehouseLookupPopupOpen
    {
        get => _isWarehouseLookupPopupOpen;
        set => SetProperty(ref _isWarehouseLookupPopupOpen, value);
    }

    public bool IsDestinationWarehouseLookupPopupOpen
    {
        get => _isDestinationWarehouseLookupPopupOpen;
        set => SetProperty(ref _isDestinationWarehouseLookupPopupOpen, value);
    }

    public InventoryLookupOption? SelectedItemLookupSuggestion
    {
        get => _selectedItemLookupSuggestion;
        set => SetProperty(ref _selectedItemLookupSuggestion, value);
    }

    public InventoryLookupOption? SelectedExpenseAccountLookupSuggestion
    {
        get => _selectedExpenseAccountLookupSuggestion;
        set => SetProperty(ref _selectedExpenseAccountLookupSuggestion, value);
    }

    public InventoryLookupOption? SelectedWarehouseLookupSuggestion
    {
        get => _selectedWarehouseLookupSuggestion;
        set => SetProperty(ref _selectedWarehouseLookupSuggestion, value);
    }

    public InventoryLookupOption? SelectedDestinationWarehouseLookupSuggestion
    {
        get => _selectedDestinationWarehouseLookupSuggestion;
        set => SetProperty(ref _selectedDestinationWarehouseLookupSuggestion, value);
    }

    public void ApplyResolvedItem(ManagedInventoryItem item)
    {
        ItemId = item.Id;
        ItemCode = item.Code?.Trim() ?? string.Empty;
        ItemName = item.Name?.Trim() ?? string.Empty;
        Uom = item.Uom?.Trim() ?? string.Empty;
        ItemLookupText = ItemCode;
    }

    public void ClearItemLookupState()
    {
        ItemLookupSuggestions.Clear();
        SelectedItemLookupSuggestion = null;
        IsItemLookupPopupOpen = false;
    }

    public void ApplyResolvedExpenseAccount(ManagedAccount account)
    {
        ExpenseAccountCode = account.Code?.Trim() ?? string.Empty;
        ExpenseAccountName = account.Name?.Trim() ?? string.Empty;
        ExpenseAccountLookupText = ExpenseAccountCode;
    }

    public void ApplyResolvedWarehouse(ManagedWarehouse warehouse)
    {
        WarehouseId = warehouse.Id;
        WarehouseName = warehouse.Name?.Trim() ?? string.Empty;
        WarehouseLookupText = WarehouseName;
    }

    public void ClearWarehouseLookupState()
    {
        WarehouseLookupSuggestions.Clear();
        SelectedWarehouseLookupSuggestion = null;
        IsWarehouseLookupPopupOpen = false;
    }

    public void ApplyResolvedDestinationWarehouse(ManagedWarehouse warehouse)
    {
        DestinationWarehouseId = warehouse.Id;
        DestinationWarehouseName = warehouse.Name?.Trim() ?? string.Empty;
        DestinationWarehouseLookupText = DestinationWarehouseName;
    }

    public void ClearDestinationWarehouseLookupState()
    {
        DestinationWarehouseLookupSuggestions.Clear();
        SelectedDestinationWarehouseLookupSuggestion = null;
        IsDestinationWarehouseLookupPopupOpen = false;
    }

    public void ClearExpenseAccountLookupState()
    {
        ExpenseAccountLookupSuggestions.Clear();
        SelectedExpenseAccountLookupSuggestion = null;
        IsExpenseAccountLookupPopupOpen = false;
    }

    private void ClearResolvedItemState()
    {
        ItemId = 0;
        ItemCode = string.Empty;
        ItemName = string.Empty;
        Uom = string.Empty;
    }

    private void ClearResolvedExpenseAccountState()
    {
        ExpenseAccountCode = string.Empty;
        ExpenseAccountName = string.Empty;
    }

    private void ClearResolvedWarehouseState()
    {
        WarehouseId = null;
        WarehouseName = string.Empty;
    }

    private void ClearResolvedDestinationWarehouseState()
    {
        DestinationWarehouseId = null;
        DestinationWarehouseName = string.Empty;
    }
}
