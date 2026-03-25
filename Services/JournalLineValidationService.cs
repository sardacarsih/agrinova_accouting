using Accounting.ViewModels;

namespace Accounting.Services;

public sealed class JournalLineValidationService
{
    public Dictionary<string, ManagedAccount> BuildActiveAccountLookup(IEnumerable<ManagedAccount> accounts)
    {
        var lookup = new Dictionary<string, ManagedAccount>(StringComparer.OrdinalIgnoreCase);
        foreach (var account in accounts.Where(x => x.IsActive))
        {
            var normalizedCode = NormalizeAccountCode(account.Code);
            if (string.IsNullOrWhiteSpace(normalizedCode))
            {
                continue;
            }

            lookup[normalizedCode] = account;
        }

        return lookup;
    }

    public void SyncAccountLine(JournalLineEditor line, IReadOnlyDictionary<string, ManagedAccount> accountLookupByCode)
    {
        var normalizedCode = NormalizeAccountCode(line.AccountCode);
        if (!string.Equals(line.AccountCode, normalizedCode, StringComparison.Ordinal))
        {
            line.AccountCode = normalizedCode;
        }

        if (string.IsNullOrWhiteSpace(normalizedCode))
        {
            line.AccountName = string.Empty;
            ValidateLine(line, accountLookupByCode);
            return;
        }

        if (accountLookupByCode.TryGetValue(normalizedCode, out var account))
        {
            line.AccountName = account.Name;
            ValidateLine(line, accountLookupByCode);
            return;
        }

        line.AccountName = string.Empty;
        ValidateLine(line, accountLookupByCode);
    }

    public void ValidateLine(JournalLineEditor line, IReadOnlyDictionary<string, ManagedAccount> accountLookupByCode)
    {
        var message = GetDraftLineValidationMessage(line, accountLookupByCode);
        line.ValidationMessage = message ?? string.Empty;
        line.HasValidationError = !string.IsNullOrWhiteSpace(message);
    }

    private static bool HasNonAccountInput(JournalLineEditor line)
    {
        return
            !string.IsNullOrWhiteSpace(line.Description) ||
            line.Debit != 0m ||
            line.Credit != 0m ||
            !string.IsNullOrWhiteSpace(line.DepartmentCode) ||
            !string.IsNullOrWhiteSpace(line.ProjectCode) ||
            !string.IsNullOrWhiteSpace(line.CostCenterCode);
    }

    private static string? GetDraftLineValidationMessage(
        JournalLineEditor line,
        IReadOnlyDictionary<string, ManagedAccount> accountLookupByCode)
    {
        var normalizedCode = NormalizeAccountCode(line.AccountCode);
        if (string.IsNullOrWhiteSpace(normalizedCode))
        {
            return HasNonAccountInput(line) ? "Kode akun wajib diisi." : null;
        }

        if (!accountLookupByCode.TryGetValue(normalizedCode, out var account))
        {
            return $"Kode akun '{normalizedCode}' tidak valid.";
        }

        return string.IsNullOrWhiteSpace(account.Name) ? "Nama perkiraan kosong." : null;
    }

    private static string NormalizeAccountCode(string? code)
    {
        return string.IsNullOrWhiteSpace(code)
            ? string.Empty
            : code.Trim().ToUpperInvariant();
    }
}
