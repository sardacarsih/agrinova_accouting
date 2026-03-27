using Microsoft.Win32;
using Accounting.Infrastructure.Logging;
using Accounting.Services;

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
            ClearMasterImportErrors();
            StatusMessage = "Memvalidasi file import inventory...";

            var parseResult = _inventoryImportXlsxService.Parse(dialog.FileName);
            if (!parseResult.IsSuccess)
            {
                SetMasterImportErrors(parseResult.Errors, parseResult.Message);
                StatusMessage = BuildImportFailureStatusMessage("Validasi file import inventory gagal.", parseResult.Errors);
                ShowImportErrors(
                    "Validasi Import Master Inventory",
                    parseResult.Message,
                    parseResult.Errors);
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
                ClearMasterImportErrors();
                await LoadDataAsync(forceReload: true);
            }
            else
            {
                var resultErrors = importResult.Errors.Count > 0
                    ? (IReadOnlyCollection<InventoryImportError>)importResult.Errors
                    : [new InventoryImportError { SheetName = "Import", RowNumber = 0, Message = importResult.Message }];
                SetMasterImportErrors(resultErrors, importResult.Message);
                ShowImportErrors(
                    "Import Master Inventory",
                    importResult.Message,
                    resultErrors);
            }
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogError(
                nameof(InventoryViewModel),
                "ImportInventoryMasterDataFailed",
                $"action=import_inventory_master_data company_id={_companyId} file_path={dialog.FileName}",
                ex);
            var fallbackErrors = new[] { BuildGenericImportError("Terjadi kesalahan saat memproses import inventory.") };
            SetMasterImportErrors(fallbackErrors, "Import inventory gagal diproses.");
            StatusMessage = BuildImportFailureStatusMessage("Import inventory gagal diproses.", fallbackErrors);
            ShowImportErrors(
                "Import Master Inventory",
                "Import inventory gagal diproses.",
                fallbackErrors);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SetMasterImportErrors(
        IReadOnlyCollection<InventoryImportError>? errors,
        string summaryMessage)
    {
        MasterImportErrorPanel.SetErrors(
            errors ?? Array.Empty<InventoryImportError>(),
            BuildMasterImportPanelSummary(summaryMessage, errors ?? Array.Empty<InventoryImportError>()));
    }

    private void ClearMasterImportErrors()
    {
        MasterImportErrorPanel.Clear();
    }

    private static string BuildMasterImportPanelSummary(
        string summaryMessage,
        IReadOnlyCollection<InventoryImportError> errors)
    {
        if (errors.Count == 0)
        {
            return string.Empty;
        }

        return $"{summaryMessage} Perbaiki kategori/item yang gagal lalu ulangi import.";
    }
}
