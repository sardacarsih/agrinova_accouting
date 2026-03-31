using System.Windows;
using Microsoft.Win32;
using Accounting.Infrastructure.Logging;
using Accounting.Services;

namespace Accounting.ViewModels;

public sealed partial class MasterDataViewModel
{
    public void SetSelectedEstateHierarchyItem(object? selectedItem)
    {
        SelectedEstateHierarchyItem = selectedItem;
        SelectedEstateHierarchyEstate = selectedItem as ManagedEstate;
        SelectedEstateHierarchyDivision = selectedItem as ManagedDivision;
        SelectedEstateHierarchyBlock = selectedItem as ManagedBlock;

        OnPropertyChanged(nameof(HasSelectedEstateHierarchyItem));
        OnPropertyChanged(nameof(CanCreateDivision));
        OnPropertyChanged(nameof(CanCreateBlock));
        OnPropertyChanged(nameof(CanEditEstateHierarchy));
        OnPropertyChanged(nameof(CanDeactivateEstateHierarchy));
        OnPropertyChanged(nameof(SelectedEstateHierarchyLevelLabel));
        OnPropertyChanged(nameof(SelectedEstateHierarchyCode));
        OnPropertyChanged(nameof(SelectedEstateHierarchyName));
        OnPropertyChanged(nameof(SelectedEstateHierarchyParentLabel));
        OnPropertyChanged(nameof(SelectedEstateHierarchyStatusText));

        _newDivisionCommand.RaiseCanExecuteChanged();
        _newBlockCommand.RaiseCanExecuteChanged();
        _editEstateHierarchyCommand.RaiseCanExecuteChanged();
        _deactivateEstateHierarchyCommand.RaiseCanExecuteChanged();
    }

    private void NotifyEstateHierarchyEditorChanged()
    {
        OnPropertyChanged(nameof(CanSaveEstateHierarchy));
        OnPropertyChanged(nameof(EstateHierarchyEditorTitle));
        OnPropertyChanged(nameof(EstateHierarchyEditorSubtitle));
        _saveEstateHierarchyCommand.RaiseCanExecuteChanged();
    }

    private void OpenNewEstateEditor()
    {
        _estateHierarchyEditorEntityId = 0;
        EstateHierarchyEditorLevel = "ESTATE";
        EstateHierarchyEditorEstateCode = string.Empty;
        EstateHierarchyEditorDivisionCode = string.Empty;
        EstateHierarchyEditorCode = string.Empty;
        EstateHierarchyEditorName = string.Empty;
        EstateHierarchyEditorIsActive = true;
        IsEstateHierarchyEditorOpen = true;
        NotifyEstateHierarchyEditorChanged();
        StatusMessage = "Form estate baru siap diisi.";
    }

    private void OpenNewDivisionEditor()
    {
        if (SelectedEstateHierarchyEstate is null)
        {
            StatusMessage = "Pilih estate untuk menambah divisi.";
            return;
        }

        _estateHierarchyEditorEntityId = 0;
        EstateHierarchyEditorLevel = "DIVISION";
        EstateHierarchyEditorEstateCode = SelectedEstateHierarchyEstate.Code;
        EstateHierarchyEditorDivisionCode = string.Empty;
        EstateHierarchyEditorCode = string.Empty;
        EstateHierarchyEditorName = string.Empty;
        EstateHierarchyEditorIsActive = true;
        IsEstateHierarchyEditorOpen = true;
        NotifyEstateHierarchyEditorChanged();
        StatusMessage = $"Form divisi baru untuk estate {SelectedEstateHierarchyEstate.Code} siap diisi.";
    }

    private void OpenNewBlockEditor()
    {
        if (SelectedEstateHierarchyDivision is null)
        {
            StatusMessage = "Pilih divisi untuk menambah blok.";
            return;
        }

        _estateHierarchyEditorEntityId = 0;
        EstateHierarchyEditorLevel = "BLOCK";
        EstateHierarchyEditorEstateCode = SelectedEstateHierarchyDivision.EstateCode;
        EstateHierarchyEditorDivisionCode = SelectedEstateHierarchyDivision.Code;
        EstateHierarchyEditorCode = string.Empty;
        EstateHierarchyEditorName = string.Empty;
        EstateHierarchyEditorIsActive = true;
        IsEstateHierarchyEditorOpen = true;
        NotifyEstateHierarchyEditorChanged();
        StatusMessage = $"Form blok baru untuk divisi {SelectedEstateHierarchyDivision.Code} siap diisi.";
    }

    private void OpenEditEstateHierarchyEditor()
    {
        switch (SelectedEstateHierarchyItem)
        {
            case ManagedEstate estate:
                _estateHierarchyEditorEntityId = estate.Id;
                EstateHierarchyEditorLevel = "ESTATE";
                EstateHierarchyEditorEstateCode = string.Empty;
                EstateHierarchyEditorDivisionCode = string.Empty;
                EstateHierarchyEditorCode = estate.Code;
                EstateHierarchyEditorName = estate.Name;
                EstateHierarchyEditorIsActive = estate.IsActive;
                break;
            case ManagedDivision division:
                _estateHierarchyEditorEntityId = division.Id;
                EstateHierarchyEditorLevel = "DIVISION";
                EstateHierarchyEditorEstateCode = division.EstateCode;
                EstateHierarchyEditorDivisionCode = string.Empty;
                EstateHierarchyEditorCode = division.Code;
                EstateHierarchyEditorName = division.Name;
                EstateHierarchyEditorIsActive = division.IsActive;
                break;
            case ManagedBlock block:
                _estateHierarchyEditorEntityId = block.Id;
                EstateHierarchyEditorLevel = "BLOCK";
                EstateHierarchyEditorEstateCode = block.EstateCode;
                EstateHierarchyEditorDivisionCode = block.DivisionCode;
                EstateHierarchyEditorCode = block.Code;
                EstateHierarchyEditorName = block.Name;
                EstateHierarchyEditorIsActive = block.IsActive;
                break;
            default:
                StatusMessage = "Pilih estate, divisi, atau blok yang ingin diedit.";
                return;
        }

        IsEstateHierarchyEditorOpen = true;
        NotifyEstateHierarchyEditorChanged();
        StatusMessage = "Editor hierarchy siap digunakan.";
    }

    private void CancelEstateHierarchyEdit()
    {
        IsEstateHierarchyEditorOpen = false;
        _estateHierarchyEditorEntityId = 0;
        EstateHierarchyEditorLevel = "ESTATE";
        EstateHierarchyEditorEstateCode = string.Empty;
        EstateHierarchyEditorDivisionCode = string.Empty;
        EstateHierarchyEditorCode = string.Empty;
        EstateHierarchyEditorName = string.Empty;
        EstateHierarchyEditorIsActive = true;
        ClearEstateHierarchyImportErrors();
        NotifyEstateHierarchyEditorChanged();
    }

    private async Task SaveEstateHierarchyAsync()
    {
        if (!CanSaveEstateHierarchy)
        {
            StatusMessage = "Lengkapi data hierarchy terlebih dahulu.";
            return;
        }

        try
        {
            IsBusy = true;
            AccessOperationResult result = EstateHierarchyEditorLevel switch
            {
                "DIVISION" => await _accessControlService.SaveDivisionAsync(
                    _companyId,
                    _locationId,
                    new ManagedDivision
                    {
                        Id = _estateHierarchyEditorEntityId,
                        CompanyId = _companyId,
                        LocationId = _locationId,
                        EstateCode = EstateHierarchyEditorEstateCode,
                        Code = EstateHierarchyEditorCode,
                        Name = EstateHierarchyEditorName,
                        IsActive = EstateHierarchyEditorIsActive
                    },
                    _actorUsername),
                "BLOCK" => await _accessControlService.SaveBlockAsync(
                    _companyId,
                    _locationId,
                    new ManagedBlock
                    {
                        Id = _estateHierarchyEditorEntityId,
                        CompanyId = _companyId,
                        LocationId = _locationId,
                        EstateCode = EstateHierarchyEditorEstateCode,
                        DivisionCode = EstateHierarchyEditorDivisionCode,
                        Code = EstateHierarchyEditorCode,
                        Name = EstateHierarchyEditorName,
                        IsActive = EstateHierarchyEditorIsActive
                    },
                    _actorUsername),
                _ => await _accessControlService.SaveEstateAsync(
                    _companyId,
                    _locationId,
                    new ManagedEstate
                    {
                        Id = _estateHierarchyEditorEntityId,
                        CompanyId = _companyId,
                        LocationId = _locationId,
                        Code = EstateHierarchyEditorCode,
                        Name = EstateHierarchyEditorName,
                        IsActive = EstateHierarchyEditorIsActive
                    },
                    _actorUsername)
            };

            StatusMessage = result.Message;
            if (result.IsSuccess)
            {
                IsEstateHierarchyEditorOpen = false;
                await LoadDataAsync(forceReload: true);
            }
        }
        finally
        {
            IsBusy = false;
            NotifyEstateHierarchyEditorChanged();
        }
    }

    private async Task DeactivateEstateHierarchyAsync()
    {
        if (!CanDeactivateEstateHierarchy)
        {
            StatusMessage = "Pilih node hierarchy yang ingin dinonaktifkan.";
            return;
        }

        try
        {
            IsBusy = true;
            AccessOperationResult result = SelectedEstateHierarchyItem switch
            {
                ManagedBlock block => await _accessControlService.SoftDeleteBlockAsync(_companyId, _locationId, block.Id, _actorUsername),
                ManagedDivision division => await _accessControlService.SoftDeleteDivisionAsync(_companyId, _locationId, division.Id, _actorUsername),
                ManagedEstate estate => await _accessControlService.SoftDeleteEstateAsync(_companyId, _locationId, estate.Id, _actorUsername),
                _ => new AccessOperationResult(false, "Pilih node hierarchy yang valid.")
            };

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

    private async Task ExportEstateHierarchyAsync()
    {
        if (!CanExportEstateHierarchy)
        {
            StatusMessage = ExportEstateHierarchyTooltip;
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "Excel Workbook (*.xlsx)|*.xlsx",
            DefaultExt = "xlsx",
            AddExtension = true,
            FileName = $"MASTER_ESTATE_DIVISION_BLOK_{DateTime.Now:yyyyMMddHHmmss}.xlsx"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "Menyiapkan export estate/division/blok...";
            var workspace = await _accessControlService.GetEstateHierarchyAsync(_companyId, _locationId, includeInactive: true, actorUsername: _actorUsername);
            var result = _estateHierarchyImportExportXlsxService.Export(dialog.FileName, workspace);
            StatusMessage = result.Message;
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogError(nameof(MasterDataViewModel), "ExportEstateHierarchyFailed", $"action=export_estate_hierarchy company_id={_companyId} location_id={_locationId} file_path={dialog.FileName}", ex);
            StatusMessage = "Gagal export estate/division/blok.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ImportEstateHierarchyAsync()
    {
        if (!CanImportEstateHierarchy)
        {
            StatusMessage = ImportEstateHierarchyTooltip;
            return;
        }

        var dialog = new OpenFileDialog
        {
            Filter = "Excel Workbook (*.xlsx)|*.xlsx",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            IsBusy = true;
            ClearEstateHierarchyImportErrors();
            StatusMessage = "Memvalidasi file import estate/division/blok...";

            var parseResult = _estateHierarchyImportExportXlsxService.Parse(dialog.FileName);
            if (!parseResult.IsSuccess)
            {
                ApplyEstateHierarchyImportFailure(parseResult.Message, parseResult.Errors);
                ShowImportErrors(
                    "Import Estate/Division/Blok",
                    parseResult.Message,
                    parseResult.Errors,
                    defaultSheetName: "Hierarchy");
                return;
            }

            var importResult = await _accessControlService.ImportEstateHierarchyAsync(_companyId, _locationId, parseResult.Bundle, _actorUsername);
            StatusMessage = importResult.Message;

            if (importResult.Errors.Count > 0)
            {
                SetEstateHierarchyImportErrors(importResult.Errors, importResult.Message);
                ShowImportErrors(
                    "Import Estate/Division/Blok",
                    importResult.Message,
                    importResult.Errors,
                    defaultSheetName: "Hierarchy");
            }
            else
            {
                ClearEstateHierarchyImportErrors();
            }

            await LoadDataAsync(forceReload: true);
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogError(nameof(MasterDataViewModel), "ImportEstateHierarchyFailed", $"action=import_estate_hierarchy company_id={_companyId} location_id={_locationId} file_path={dialog.FileName}", ex);
            var fallbackErrors = new[]
            {
                new InventoryImportError { SheetName = "Hierarchy", RowNumber = 0, Message = "Terjadi kesalahan saat memproses import estate/division/blok." }
            };
            ApplyEstateHierarchyImportFailure("Import estate/division/blok gagal diproses.", fallbackErrors);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyEstateHierarchyImportFailure(string summaryMessage, IReadOnlyCollection<InventoryImportError>? errors)
    {
        SetEstateHierarchyImportErrors(errors ?? Array.Empty<InventoryImportError>(), summaryMessage);
        StatusMessage = BuildImportFailureStatusMessage(summaryMessage, errors, "Perbaiki workbook lalu ulangi import.");
    }

    private void SetEstateHierarchyImportErrors(IReadOnlyCollection<InventoryImportError> errors, string summaryMessage)
    {
        EstateHierarchyImportErrorPanel.SetErrors(errors, errors.Count == 0 ? string.Empty : $"{summaryMessage} Perbaiki baris yang gagal lalu ulangi import.");
    }

    private void ClearEstateHierarchyImportErrors()
    {
        EstateHierarchyImportErrorPanel.Clear();
    }

    private void ApplyEstateHierarchyFilter()
    {
        var keyword = (EstateHierarchySearchText ?? string.Empty).Trim();
        var activeOnly = string.Equals(SelectedEstateHierarchyStatusFilter, AccountStatusActive, StringComparison.OrdinalIgnoreCase);
        var inactiveOnly = string.Equals(SelectedEstateHierarchyStatusFilter, AccountStatusInactive, StringComparison.OrdinalIgnoreCase);

        var filtered = new List<ManagedEstate>();
        foreach (var estate in EstateHierarchyEstates)
        {
            var clone = BuildVisibleEstate(estate, keyword, activeOnly, inactiveOnly, ancestorMatched: false);
            if (clone is not null)
            {
                filtered.Add(clone);
            }
        }

        ReplaceCollection(VisibleEstateHierarchyEstates, filtered.OrderBy(x => x.Code));
        OnPropertyChanged(nameof(VisibleEstatesCount));
        OnPropertyChanged(nameof(VisibleDivisionsCount));
        OnPropertyChanged(nameof(VisibleBlocksCount));

        if (!ContainsSelectedHierarchyItem(SelectedEstateHierarchyItem))
        {
            SetSelectedEstateHierarchyItem(VisibleEstateHierarchyEstates.FirstOrDefault());
        }
    }

    private ManagedEstate? BuildVisibleEstate(ManagedEstate source, string keyword, bool activeOnly, bool inactiveOnly, bool ancestorMatched)
    {
        var selfVisible = MatchesHierarchyStatus(source.IsActive, activeOnly, inactiveOnly) &&
                          (ancestorMatched || string.IsNullOrWhiteSpace(keyword) || MatchesKeyword(source.Code, source.Name, source.Code, keyword));

        var divisionClones = new List<ManagedDivision>();
        foreach (var division in source.Divisions)
        {
            var clone = BuildVisibleDivision(division, keyword, activeOnly, inactiveOnly, selfVisible);
            if (clone is not null)
            {
                divisionClones.Add(clone);
            }
        }

        if (!selfVisible && divisionClones.Count == 0)
        {
            return null;
        }

        return new ManagedEstate
        {
            Id = source.Id,
            CompanyId = source.CompanyId,
            LocationId = source.LocationId,
            Code = source.Code,
            Name = source.Name,
            IsActive = source.IsActive,
            Divisions = divisionClones
        };
    }

    private ManagedDivision? BuildVisibleDivision(ManagedDivision source, string keyword, bool activeOnly, bool inactiveOnly, bool ancestorMatched)
    {
        var selfVisible = MatchesHierarchyStatus(source.IsActive, activeOnly, inactiveOnly) &&
                          (ancestorMatched || string.IsNullOrWhiteSpace(keyword) || MatchesKeyword(source.Code, source.Name, $"{source.EstateCode}-{source.Code}", keyword));

        var blockClones = new List<ManagedBlock>();
        foreach (var block in source.Blocks)
        {
            if (!MatchesHierarchyStatus(block.IsActive, activeOnly, inactiveOnly))
            {
                continue;
            }

            if (ancestorMatched || selfVisible || string.IsNullOrWhiteSpace(keyword) || MatchesKeyword(block.Code, block.Name, block.CostCenterCode, keyword))
            {
                blockClones.Add(CloneBlock(block));
            }
        }

        if (!selfVisible && blockClones.Count == 0)
        {
            return null;
        }

        return new ManagedDivision
        {
            Id = source.Id,
            EstateId = source.EstateId,
            CompanyId = source.CompanyId,
            LocationId = source.LocationId,
            EstateCode = source.EstateCode,
            EstateName = source.EstateName,
            Code = source.Code,
            Name = source.Name,
            IsActive = source.IsActive,
            Blocks = blockClones
        };
    }

    private bool ContainsSelectedHierarchyItem(object? selectedItem)
    {
        return selectedItem switch
        {
            ManagedBlock block => VisibleEstateHierarchyEstates.Any(x => x.Divisions.Any(y => y.Blocks.Any(z => z.Id == block.Id))),
            ManagedDivision division => VisibleEstateHierarchyEstates.Any(x => x.Divisions.Any(y => y.Id == division.Id)),
            ManagedEstate estate => VisibleEstateHierarchyEstates.Any(x => x.Id == estate.Id),
            _ => VisibleEstateHierarchyEstates.Count > 0
        };
    }

    private static bool MatchesHierarchyStatus(bool isActive, bool activeOnly, bool inactiveOnly)
    {
        if (activeOnly)
        {
            return isActive;
        }

        if (inactiveOnly)
        {
            return !isActive;
        }

        return true;
    }

    private static bool MatchesKeyword(string primaryCode, string primaryName, string compositeCode, string keyword)
    {
        return primaryCode.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
               primaryName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
               compositeCode.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private static ManagedBlock CloneBlock(ManagedBlock source)
    {
        return new ManagedBlock
        {
            Id = source.Id,
            EstateId = source.EstateId,
            DivisionId = source.DivisionId,
            CompanyId = source.CompanyId,
            LocationId = source.LocationId,
            EstateCode = source.EstateCode,
            EstateName = source.EstateName,
            DivisionCode = source.DivisionCode,
            DivisionName = source.DivisionName,
            Code = source.Code,
            Name = source.Name,
            IsActive = source.IsActive
        };
    }
}
