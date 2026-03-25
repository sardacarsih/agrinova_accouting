using Accounting.Services;

namespace Accounting.ViewModels;

public sealed partial class InventoryViewModel
{
    private ManagedInventoryUnit? _selectedUnit;

    public ManagedInventoryUnit? SelectedUnit
    {
        get => _selectedUnit;
        set
        {
            if (!SetProperty(ref _selectedUnit, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasSelectedUnit));
            RaiseInventoryActionStateChanged();
        }
    }

    public bool HasSelectedUnit => SelectedUnit is not null && SelectedUnit.Id > 0;

    public bool CanCreateUnit => CanCreateMasterEntity(_canCreateUnit);

    public bool CanSaveUnit => CanSaveMasterEntity(SelectedUnit?.Id, _canCreateUnit, _canUpdateUnit);

    public bool CanDeactivateUnit => CanDeactivateMasterEntity(SelectedUnit?.Id, _canDeleteUnit);

    public string NewUnitTooltip => BuildNewMasterEntityTooltip("satuan", _canCreateUnit);

    public string SaveUnitTooltip => BuildSaveMasterEntityTooltip(
        "satuan",
        SelectedUnit?.Id,
        _canCreateUnit,
        _canUpdateUnit);

    public string DeactivateUnitTooltip => BuildDeactivateMasterEntityTooltip(
        "satuan",
        SelectedUnit?.Id,
        _canDeleteUnit);

    private void NewUnit()
    {
        if (!CanCreateUnit)
        {
            StatusMessage = NewUnitTooltip;
            return;
        }

        SelectedUnit = new ManagedInventoryUnit
        {
            Id = 0,
            CompanyId = _companyId,
            Code = string.Empty,
            Name = string.Empty,
            IsActive = true
        };

        StatusMessage = "Input satuan baru siap.";
    }

    private async Task SaveUnitAsync()
    {
        if (!CanSaveUnit || SelectedUnit is null)
        {
            StatusMessage = SaveUnitTooltip;
            return;
        }

        try
        {
            IsBusy = true;
            var result = await _accessControlService.SaveInventoryUnitAsync(_companyId, SelectedUnit, _actorUsername);
            StatusMessage = result.Message;

            if (result.IsSuccess)
            {
                await LoadDataAsync(forceReload: true);
                SelectedUnit = Units.FirstOrDefault(u => u.Id == result.EntityId) ?? Units.FirstOrDefault();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DeactivateUnitAsync()
    {
        if (!CanDeactivateUnit || SelectedUnit is null || SelectedUnit.Id <= 0)
        {
            StatusMessage = DeactivateUnitTooltip;
            return;
        }

        try
        {
            IsBusy = true;
            var result = await _accessControlService.SoftDeleteInventoryUnitAsync(_companyId, SelectedUnit.Id, _actorUsername);
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
