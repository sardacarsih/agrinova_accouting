using Accounting.Services;

namespace Accounting.ViewModels;

public sealed partial class InventoryViewModel
{
    private ManagedWarehouse? _selectedWarehouse;

    public ManagedWarehouse? SelectedWarehouse
    {
        get => _selectedWarehouse;
        set
        {
            if (!SetProperty(ref _selectedWarehouse, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasSelectedWarehouse));
            RaiseInventoryActionStateChanged();
        }
    }

    public bool HasSelectedWarehouse => SelectedWarehouse is not null && SelectedWarehouse.Id > 0;

    public bool CanCreateWarehouse => CanCreateMasterEntity(_canCreateWarehouse);

    public bool CanSaveWarehouse => CanSaveMasterEntity(SelectedWarehouse?.Id, _canCreateWarehouse, _canUpdateWarehouse);

    public bool CanDeactivateWarehouse => CanDeactivateMasterEntity(SelectedWarehouse?.Id, _canDeleteWarehouse);

    public string NewWarehouseTooltip => BuildNewMasterEntityTooltip("gudang", _canCreateWarehouse);

    public string SaveWarehouseTooltip => BuildSaveMasterEntityTooltip(
        "gudang",
        SelectedWarehouse?.Id,
        _canCreateWarehouse,
        _canUpdateWarehouse);

    public string DeactivateWarehouseTooltip => BuildDeactivateMasterEntityTooltip(
        "gudang",
        SelectedWarehouse?.Id,
        _canDeleteWarehouse);

    private void NewWarehouse()
    {
        if (!CanCreateWarehouse)
        {
            StatusMessage = NewWarehouseTooltip;
            return;
        }

        SelectedWarehouse = new ManagedWarehouse
        {
            Id = 0,
            CompanyId = _companyId,
            Code = string.Empty,
            Name = string.Empty,
            LocationId = _locationId,
            IsActive = true
        };

        StatusMessage = "Input gudang baru siap.";
    }

    private async Task SaveWarehouseAsync()
    {
        if (!CanSaveWarehouse || SelectedWarehouse is null)
        {
            StatusMessage = SaveWarehouseTooltip;
            return;
        }

        try
        {
            IsBusy = true;
            var result = await _accessControlService.SaveWarehouseAsync(_companyId, SelectedWarehouse, _actorUsername);
            StatusMessage = result.Message;

            if (result.IsSuccess)
            {
                await LoadDataAsync(forceReload: true);
                SelectedWarehouse = Warehouses.FirstOrDefault(w => w.Id == result.EntityId) ?? Warehouses.FirstOrDefault();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DeactivateWarehouseAsync()
    {
        if (!CanDeactivateWarehouse || SelectedWarehouse is null || SelectedWarehouse.Id <= 0)
        {
            StatusMessage = DeactivateWarehouseTooltip;
            return;
        }

        try
        {
            IsBusy = true;
            var result = await _accessControlService.SoftDeleteWarehouseAsync(_companyId, SelectedWarehouse.Id, _actorUsername);
            StatusMessage = result.Message;

            if (result.IsSuccess)
            {
                await LoadDataAsync(forceReload: true);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }
}
