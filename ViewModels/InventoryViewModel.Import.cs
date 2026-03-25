using Microsoft.Win32;
using Accounting.Infrastructure.Logging;

namespace Accounting.ViewModels;

public sealed partial class InventoryViewModel
{
    private void DownloadInventoryImportTemplate()
    {
        if (!CanDownloadInventoryImportTemplate)
        {
            StatusMessage = DownloadInventoryImportTemplateTooltip;
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "Excel Workbook (*.xlsx)|*.xlsx",
            DefaultExt = "xlsx",
            AddExtension = true,
            FileName = $"INVENTORY_IMPORT_TEMPLATE_{DateTime.Now:yyyyMMddHHmmss}.xlsx"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var result = _inventoryImportXlsxService.CreateTemplate(dialog.FileName);
        StatusMessage = result.Message;
    }

    private async Task ImportInventoryMasterDataAsync()
    {
        if (!CanImportInventoryMasterData)
        {
            StatusMessage = ImportInventoryMasterDataTooltip;
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
            StatusMessage = "Memvalidasi file import inventory...";

            var parseResult = _inventoryImportXlsxService.Parse(dialog.FileName);
            if (!parseResult.IsSuccess)
            {
                StatusMessage = parseResult.Message;
                return;
            }

            StatusMessage = "Memproses import inventory...";
            var importResult = await _accessControlService.ImportInventoryMasterDataAsync(
                _companyId,
                parseResult.Bundle,
                _actorUsername);

            StatusMessage = importResult.Message;
            if (importResult.IsSuccess)
            {
                await LoadDataAsync(forceReload: true);
            }
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogError(
                nameof(InventoryViewModel),
                "ImportInventoryMasterDataFailed",
                $"action=import_inventory_master_data company_id={_companyId} file_path={dialog.FileName}",
                ex);
            StatusMessage = "Import inventory gagal diproses.";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
