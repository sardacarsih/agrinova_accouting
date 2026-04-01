using System.Text;
using System.Windows;
using Microsoft.Win32;
using Accounting.Infrastructure.Logging;
using Accounting.Services;

namespace Accounting.ViewModels;

public sealed partial class MasterDataViewModel
{
    private async Task ExportAccountsAsync()
    {
        if (!CanExportAccounts)
        {
            StatusMessage = ExportAccountsTooltip;
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "Excel Workbook (*.xlsx)|*.xlsx",
            DefaultExt = "xlsx",
            AddExtension = true,
            FileName = $"MASTER_AKUN_{DateTime.Now:yyyyMMddHHmmss}.xlsx"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "Menyiapkan export master akun...";
            var accounts = await _accessControlService.GetAccountsAsync(_companyId, includeInactive: true, _actorUsername);
            var result = _accountImportExportXlsxService.Export(dialog.FileName, accounts);
            StatusMessage = result.Message;
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogError(
                nameof(MasterDataViewModel),
                "ExportMasterAccountsFailed",
                $"action=export_master_accounts company_id={_companyId} file_path={dialog.FileName}",
                ex);
            StatusMessage = "Gagal export master akun.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ImportAccountsAsync()
    {
        if (!CanImportAccounts)
        {
            StatusMessage = ImportAccountsTooltip;
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
            ClearAccountImportErrors();
            StatusMessage = "Memvalidasi file import master akun...";

            var parseResult = _accountImportExportXlsxService.Parse(dialog.FileName);
            if (!parseResult.IsSuccess)
            {
                ApplyAccountImportValidationFailure(parseResult);
                ShowImportErrors(
                    "Validasi Import Master Akun",
                    parseResult.Message,
                    parseResult.Errors);
                return;
            }

            StatusMessage = "Memproses import master akun...";
            var importResult = await _accessControlService.ImportAccountMasterDataAsync(
                _companyId,
                parseResult.Bundle,
                _actorUsername);

            await ApplyAccountImportExecutionResultAsync(importResult);
            if (!importResult.IsSuccess)
            {
                var resultErrors = ResolveAccountImportErrors(importResult);
                ShowImportErrors(
                    "Import Master Akun",
                    importResult.Message,
                    resultErrors);
            }
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogError(
                nameof(MasterDataViewModel),
                "ImportMasterAccountsFailed",
                $"action=import_master_accounts company_id={_companyId} file_path={dialog.FileName}",
                ex);
            var fallbackErrors = new[] { BuildGenericImportError("Terjadi kesalahan saat memproses import master akun.") };
            ApplyAccountImportUnexpectedFailure("Import master akun gagal diproses.", fallbackErrors);
            ShowImportErrors(
                "Import Master Akun",
                "Import master akun gagal diproses.",
                fallbackErrors);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyAccountImportValidationFailure(AccountImportParseResult parseResult)
    {
        SetAccountImportErrors(parseResult.Errors, parseResult.Message);
        StatusMessage = BuildImportFailureStatusMessage("Validasi file import master akun gagal.", parseResult.Errors);
    }

    private async Task ApplyAccountImportExecutionResultAsync(
        AccountImportExecutionResult importResult,
        bool reloadData = true)
    {
        StatusMessage = importResult.Message;
        if (importResult.IsSuccess)
        {
            ClearAccountImportErrors();
            if (reloadData)
            {
                await LoadDataAsync(forceReload: true);
            }

            return;
        }

        var resultErrors = ResolveAccountImportErrors(importResult);
        SetAccountImportErrors(resultErrors, importResult.Message);
    }

    private void ApplyAccountImportUnexpectedFailure(
        string summaryMessage,
        IReadOnlyCollection<InventoryImportError> errors)
    {
        SetAccountImportErrors(errors, summaryMessage);
        StatusMessage = BuildImportFailureStatusMessage(summaryMessage, errors);
    }

    private void SetAccountImportErrors(
        IReadOnlyCollection<InventoryImportError>? errors,
        string summaryMessage)
    {
        AccountImportErrorPanel.SetErrors(
            errors ?? Array.Empty<InventoryImportError>(),
            BuildAccountImportPanelSummary(summaryMessage, errors ?? Array.Empty<InventoryImportError>()));
    }

    private void ClearAccountImportErrors()
    {
        AccountImportErrorPanel.Clear();
    }

    private static IReadOnlyCollection<InventoryImportError> ResolveAccountImportErrors(AccountImportExecutionResult importResult)
    {
        return importResult.Errors.Count > 0
            ? importResult.Errors
            : [BuildGenericImportError(importResult.Message)];
    }

    private static string BuildAccountImportPanelSummary(
        string summaryMessage,
        IReadOnlyCollection<InventoryImportError> errors)
    {
        if (errors.Count == 0)
        {
            return string.Empty;
        }

        return $"{summaryMessage} Perbaiki akun yang gagal lalu ulangi import.";
    }

    private static InventoryImportError BuildGenericImportError(string message, string sheetName = "Accounts")
    {
        return new InventoryImportError
        {
            SheetName = sheetName,
            RowNumber = 0,
            Message = message
        };
    }

    private static string BuildImportFailureStatusMessage(
        string fallbackMessage,
        IReadOnlyCollection<InventoryImportError>? errors,
        string? extraHint = null)
    {
        if (errors is null || errors.Count == 0)
        {
            return fallbackMessage;
        }

        var message = $"{fallbackMessage} ({errors.Count} error). Lihat detail untuk daftar baris.";
        return string.IsNullOrWhiteSpace(extraHint)
            ? message
            : $"{message} {extraHint}";
    }

    private static void ShowImportErrors(
        string title,
        string summaryMessage,
        IReadOnlyCollection<InventoryImportError>? errors,
        string? hintMessage = null,
        bool includeSheetName = true,
        string defaultSheetName = "Accounts")
    {
        if (errors is null || errors.Count == 0)
        {
            MessageBox.Show(summaryMessage, title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var builder = new StringBuilder();
        builder.AppendLine(summaryMessage);
        builder.AppendLine();
        builder.AppendLine($"Total error: {errors.Count}");

        if (!string.IsNullOrWhiteSpace(hintMessage))
        {
            builder.AppendLine(hintMessage);
        }

        builder.AppendLine();
        builder.AppendLine("Detail:");

        var previewErrors = errors.Take(12).ToList();
        for (var index = 0; index < previewErrors.Count; index++)
        {
            var error = previewErrors[index];
            builder.Append(index + 1).Append(". ");
            if (includeSheetName)
            {
                builder.Append(string.IsNullOrWhiteSpace(error.SheetName) ? defaultSheetName : error.SheetName)
                    .Append(" baris ");
            }
            else
            {
                builder.Append("Baris ");
            }

            builder.Append(error.RowNumber <= 0 ? 1 : error.RowNumber)
                .Append(": ")
                .AppendLine(error.Message);
        }

        if (errors.Count > previewErrors.Count)
        {
            builder.AppendLine();
            builder.Append("Masih ada ")
                .Append(errors.Count - previewErrors.Count)
                .AppendLine(" error tambahan. Perbaiki file lalu impor ulang.");
        }

        MessageBox.Show(builder.ToString(), title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
