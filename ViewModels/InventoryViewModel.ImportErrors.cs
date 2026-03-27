using System.Text;
using System.Windows;
using Accounting.Services;

namespace Accounting.ViewModels;

public sealed partial class InventoryViewModel
{
    private static InventoryImportError BuildGenericImportError(string message, string sheetName = "Import")
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
        string defaultSheetName = "Import")
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
