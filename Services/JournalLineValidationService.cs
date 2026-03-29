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

    public Dictionary<string, ManagedCostCenter> BuildCostCenterLookup(IEnumerable<ManagedCostCenter> costCenters)
    {
        var lookup = new Dictionary<string, ManagedCostCenter>(StringComparer.OrdinalIgnoreCase);
        foreach (var costCenter in costCenters.Where(x => x.IsActive))
        {
            var normalizedCode = NormalizeCostCenterCode(costCenter.CostCenterCode);
            if (string.IsNullOrWhiteSpace(normalizedCode))
            {
                continue;
            }

            lookup[normalizedCode] = costCenter;
        }

        return lookup;
    }

    public void SyncAccountLine(
        JournalLineEditor line,
        IReadOnlyDictionary<string, ManagedAccount> accountLookupByCode,
        IReadOnlyDictionary<string, ManagedCostCenter>? costCenterLookupByCode = null)
    {
        var normalizedCode = NormalizeAccountCode(line.AccountCode);
        if (!string.Equals(line.AccountCode, normalizedCode, StringComparison.Ordinal))
        {
            line.AccountCode = normalizedCode;
        }

        if (string.IsNullOrWhiteSpace(normalizedCode))
        {
            line.AccountName = string.Empty;
            SyncCostCenterLine(line, costCenterLookupByCode);
            ValidateLine(line, accountLookupByCode, costCenterLookupByCode);
            return;
        }

        if (accountLookupByCode.TryGetValue(normalizedCode, out var account))
        {
            line.AccountName = account.Name;
            SyncCostCenterLine(line, costCenterLookupByCode);
            ValidateLine(line, accountLookupByCode, costCenterLookupByCode);
            return;
        }

        line.AccountName = string.Empty;
        SyncCostCenterLine(line, costCenterLookupByCode);
        ValidateLine(line, accountLookupByCode, costCenterLookupByCode);
    }

    public void ValidateLine(
        JournalLineEditor line,
        IReadOnlyDictionary<string, ManagedAccount> accountLookupByCode,
        IReadOnlyDictionary<string, ManagedCostCenter>? costCenterLookupByCode = null)
    {
        var message = GetDraftLineValidationMessage(line, accountLookupByCode, costCenterLookupByCode);
        line.ValidationMessage = message ?? string.Empty;
        line.HasValidationError = !string.IsNullOrWhiteSpace(message);
    }

    private static void SyncCostCenterLine(
        JournalLineEditor line,
        IReadOnlyDictionary<string, ManagedCostCenter>? costCenterLookupByCode)
    {
        var normalizedCode = NormalizeCostCenterCode(line.CostCenterCode);
        if (!string.Equals(line.CostCenterCode, normalizedCode, StringComparison.Ordinal))
        {
            line.CostCenterCode = normalizedCode;
        }

        if (string.IsNullOrWhiteSpace(normalizedCode) || costCenterLookupByCode is null)
        {
            line.CostCenterId = null;
            return;
        }

        if (costCenterLookupByCode.TryGetValue(normalizedCode, out var costCenter))
        {
            line.CostCenterId = costCenter.Id;
            line.CostCenterCode = costCenter.CostCenterCode;
            return;
        }

        line.CostCenterId = null;
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
        IReadOnlyDictionary<string, ManagedAccount> accountLookupByCode,
        IReadOnlyDictionary<string, ManagedCostCenter>? costCenterLookupByCode)
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

        if (string.IsNullOrWhiteSpace(account.Name))
        {
            return "Nama perkiraan kosong.";
        }

        var normalizedCostCenterCode = NormalizeCostCenterCode(line.CostCenterCode);
        if (account.RequiresCostCenter && string.IsNullOrWhiteSpace(normalizedCostCenterCode))
        {
            return $"Akun '{normalizedCode}' wajib memakai cost center.";
        }

        if (!string.IsNullOrWhiteSpace(normalizedCostCenterCode))
        {
            if (costCenterLookupByCode is null ||
                !costCenterLookupByCode.TryGetValue(normalizedCostCenterCode, out var costCenter))
            {
                return account.RequiresCostCenter || line.CostCenterId.HasValue
                    ? $"Cost center '{normalizedCostCenterCode}' tidak valid."
                    : null;
            }

            if (!costCenter.IsActive)
            {
                return $"Cost center '{normalizedCostCenterCode}' nonaktif.";
            }

            if (!costCenter.IsPosting)
            {
                return $"Cost center '{normalizedCostCenterCode}' bukan level posting.";
            }
        }

        return null;
    }

    private static string NormalizeAccountCode(string? code)
    {
        return string.IsNullOrWhiteSpace(code)
            ? string.Empty
            : code.Trim().ToUpperInvariant();
    }

    private static string NormalizeCostCenterCode(string? code)
    {
        return string.IsNullOrWhiteSpace(code)
            ? string.Empty
            : code.Trim().ToUpperInvariant();
    }
}
