using System.Collections.ObjectModel;
using System.Windows.Input;
using Accounting.Infrastructure;
using Accounting.Services;

namespace Accounting.ViewModels;

public sealed partial class InventoryViewModel
{
    private ManagedStorageLocation? _selectedStorageLocation;

    public ObservableCollection<ManagedStorageLocation> StorageLocations { get; }

    public ICommand NewStorageLocationCommand { get; private set; } = null!;

    public ICommand SaveStorageLocationCommand { get; private set; } = null!;

    public ICommand DeactivateStorageLocationCommand { get; private set; } = null!;

    public ManagedStorageLocation? SelectedStorageLocation
    {
        get => _selectedStorageLocation;
        set
        {
            if (!SetProperty(ref _selectedStorageLocation, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasSelectedStorageLocation));
            RaiseInventoryActionStateChanged();
        }
    }

    public bool HasSelectedStorageLocation => SelectedStorageLocation is not null && SelectedStorageLocation.Id > 0;

    public bool CanCreateStorageLocation => CanCreateMasterEntity(_canCreateWarehouse);

    public bool CanSaveStorageLocation => CanSaveMasterEntity(SelectedStorageLocation?.Id, _canCreateWarehouse, _canUpdateWarehouse);

    public bool CanDeactivateStorageLocation => CanDeactivateMasterEntity(SelectedStorageLocation?.Id, _canDeleteWarehouse);

    public string NewStorageLocationTooltip => BuildNewMasterEntityTooltip("lokasi penyimpanan", _canCreateWarehouse);

    public string SaveStorageLocationTooltip => BuildSaveMasterEntityTooltip(
        "lokasi penyimpanan",
        SelectedStorageLocation?.Id,
        _canCreateWarehouse,
        _canUpdateWarehouse);

    public string DeactivateStorageLocationTooltip => BuildDeactivateMasterEntityTooltip(
        "lokasi penyimpanan",
        SelectedStorageLocation?.Id,
        _canDeleteWarehouse);

    private void InitializeStorageLocationCommands()
    {
        NewStorageLocationCommand = new RelayCommand(NewStorageLocation);
        SaveStorageLocationCommand = new RelayCommand(() => _ = SaveStorageLocationAsync());
        DeactivateStorageLocationCommand = new RelayCommand(() => _ = DeactivateStorageLocationAsync());
    }

    private void NewStorageLocation()
    {
        if (!CanCreateStorageLocation)
        {
            StatusMessage = NewStorageLocationTooltip;
            return;
        }

        var defaultWarehouse = Warehouses.FirstOrDefault(warehouse => warehouse.IsActive) ?? Warehouses.FirstOrDefault();
        SelectedStorageLocation = new ManagedStorageLocation
        {
            Id = 0,
            CompanyId = _companyId,
            LocationId = defaultWarehouse?.LocationId ?? _locationId,
            LocationName = defaultWarehouse?.LocationName ?? string.Empty,
            WarehouseId = defaultWarehouse?.Id ?? 0,
            WarehouseName = defaultWarehouse?.Name ?? string.Empty,
            Code = string.Empty,
            Name = string.Empty,
            IsActive = true
        };

        StatusMessage = defaultWarehouse is null
            ? "Input lokasi penyimpanan baru siap. Pilih gudang terlebih dahulu."
            : "Input lokasi penyimpanan baru siap.";
    }

    private async Task SaveStorageLocationAsync()
    {
        if (!CanSaveStorageLocation || SelectedStorageLocation is null)
        {
            StatusMessage = SaveStorageLocationTooltip;
            return;
        }

        var warehouse = Warehouses.FirstOrDefault(candidate => candidate.Id == SelectedStorageLocation.WarehouseId);
        if (warehouse is not null)
        {
            SelectedStorageLocation.LocationId = warehouse.LocationId;
            SelectedStorageLocation.LocationName = warehouse.LocationName;
            SelectedStorageLocation.WarehouseName = warehouse.Name;
        }

        try
        {
            IsBusy = true;
            var result = await _accessControlService.SaveStorageLocationAsync(_companyId, SelectedStorageLocation, _actorUsername);
            StatusMessage = result.Message;

            if (result.IsSuccess)
            {
                await LoadDataAsync(forceReload: true);
                SelectedStorageLocation = StorageLocations.FirstOrDefault(location => location.Id == result.EntityId) ?? StorageLocations.FirstOrDefault();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DeactivateStorageLocationAsync()
    {
        if (!CanDeactivateStorageLocation || SelectedStorageLocation is null || SelectedStorageLocation.Id <= 0)
        {
            StatusMessage = DeactivateStorageLocationTooltip;
            return;
        }

        try
        {
            IsBusy = true;
            var result = await _accessControlService.SoftDeleteStorageLocationAsync(_companyId, SelectedStorageLocation.Id, _actorUsername);
            StatusMessage = result.Message;

            if (result.IsSuccess)
            {
                await LoadDataAsync(forceReload: true);
                SelectedStorageLocation = StorageLocations.FirstOrDefault();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }
}
