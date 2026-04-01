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

    public Dictionary<string, ManagedSubledgerReference> BuildSubledgerLookup(IEnumerable<ManagedSubledgerReference> subledgers)
    {
        var lookup = new Dictionary<string, ManagedSubledgerReference>(StringComparer.OrdinalIgnoreCase);
        foreach (var subledger in (subledgers ?? Array.Empty<ManagedSubledgerReference>()).Where(x => x.IsActive))
        {
            var normalizedType = NormalizeSubledgerType(subledger.SubledgerType);
            var normalizedCode = NormalizeSubledgerCode(subledger.Code);
            if (string.IsNullOrWhiteSpace(normalizedType) || string.IsNullOrWhiteSpace(normalizedCode))
            {
                continue;
            }

            lookup[BuildSubledgerLookupKey(normalizedType, normalizedCode)] = subledger;
        }

        return lookup;
    }

    public void SyncAccountLine(
        JournalLineEditor line,
        IReadOnlyDictionary<string, ManagedAccount> accountLookupByCode,
        IReadOnlyDictionary<string, ManagedCostCenter>? costCenterLookupByCode = null,
        IReadOnlyDictionary<string, ManagedSubledgerReference>? subledgerLookupByKey = null)
    {
        var normalizedCode = NormalizeAccountCode(line.AccountCode);
        if (!string.Equals(line.AccountCode, normalizedCode, StringComparison.Ordinal))
        {
            line.AccountCode = normalizedCode;
        }

        if (string.IsNullOrWhiteSpace(normalizedCode))
        {
            line.AccountName = string.Empty;
            SyncSubledgerLine(line, account: null, subledgerLookupByKey);
            SyncCostCenterLine(line, costCenterLookupByCode);
            ValidateLine(line, accountLookupByCode, costCenterLookupByCode, subledgerLookupByKey);
            return;
        }

        if (accountLookupByCode.TryGetValue(normalizedCode, out var account))
        {
            line.AccountName = account.Name;
            SyncSubledgerLine(line, account, subledgerLookupByKey);
            SyncCostCenterLine(line, costCenterLookupByCode);
            ValidateLine(line, accountLookupByCode, costCenterLookupByCode, subledgerLookupByKey);
            return;
        }

        line.AccountName = string.Empty;
        SyncSubledgerLine(line, account: null, subledgerLookupByKey);
        SyncCostCenterLine(line, costCenterLookupByCode);
        ValidateLine(line, accountLookupByCode, costCenterLookupByCode, subledgerLookupByKey);
    }

    public void ValidateLine(
        JournalLineEditor line,
        IReadOnlyDictionary<string, ManagedAccount> accountLookupByCode,
        IReadOnlyDictionary<string, ManagedCostCenter>? costCenterLookupByCode = null,
        IReadOnlyDictionary<string, ManagedSubledgerReference>? subledgerLookupByKey = null)
    {
        var message = GetDraftLineValidationMessage(line, accountLookupByCode, costCenterLookupByCode, subledgerLookupByKey);
        line.ValidationMessage = message ?? string.Empty;
        line.HasValidationError = !string.IsNullOrWhiteSpace(message);
    }

    private static void SyncSubledgerLine(
        JournalLineEditor line,
        ManagedAccount? account,
        IReadOnlyDictionary<string, ManagedSubledgerReference>? subledgerLookupByKey)
    {
        var requiredType = account?.RequiresSubledger == true
            ? NormalizeSubledgerType(account.AllowedSubledgerType)
            : string.Empty;

        if (string.IsNullOrWhiteSpace(requiredType))
        {
            line.SubledgerType = string.Empty;
            line.SubledgerId = null;
            line.SubledgerCode = string.Empty;
            line.SubledgerName = string.Empty;
            return;
        }

        var normalizedCode = NormalizeSubledgerCode(line.SubledgerCode);
        line.SubledgerType = requiredType;
        if (!string.Equals(line.SubledgerCode, normalizedCode, StringComparison.Ordinal))
        {
            line.SubledgerCode = normalizedCode;
        }

        if (string.IsNullOrWhiteSpace(normalizedCode) || subledgerLookupByKey is null)
        {
            line.SubledgerId = null;
            line.SubledgerName = string.Empty;
            return;
        }

        if (subledgerLookupByKey.TryGetValue(BuildSubledgerLookupKey(requiredType, normalizedCode), out var subledger))
        {
            line.SubledgerId = subledger.Id;
            line.SubledgerCode = subledger.Code;
            line.SubledgerName = subledger.Name;
            return;
        }

        line.SubledgerId = null;
        line.SubledgerName = string.Empty;
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
            line.BlockId = null;
            return;
        }

        if (costCenterLookupByCode.TryGetValue(normalizedCode, out var costCenter))
        {
            line.CostCenterId = null;
            line.BlockId = costCenter.BlockId ?? costCenter.Id;
            line.CostCenterCode = costCenter.CostCenterCode;
            return;
        }

        line.CostCenterId = null;
        line.BlockId = null;
    }

    private static bool HasNonAccountInput(JournalLineEditor line)
    {
        return
            !string.IsNullOrWhiteSpace(line.Description) ||
            line.Debit != 0m ||
            line.Credit != 0m ||
            !string.IsNullOrWhiteSpace(line.DepartmentCode) ||
            !string.IsNullOrWhiteSpace(line.ProjectCode) ||
            !string.IsNullOrWhiteSpace(line.SubledgerCode) ||
            !string.IsNullOrWhiteSpace(line.CostCenterCode);
    }

    private static string? GetDraftLineValidationMessage(
        JournalLineEditor line,
        IReadOnlyDictionary<string, ManagedAccount> accountLookupByCode,
        IReadOnlyDictionary<string, ManagedCostCenter>? costCenterLookupByCode,
        IReadOnlyDictionary<string, ManagedSubledgerReference>? subledgerLookupByKey)
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

        var normalizedSubledgerType = NormalizeSubledgerType(line.SubledgerType);
        var normalizedSubledgerCode = NormalizeSubledgerCode(line.SubledgerCode);
        if (account.RequiresSubledger)
        {
            var requiredType = NormalizeSubledgerType(account.AllowedSubledgerType);
            if (string.IsNullOrWhiteSpace(requiredType))
            {
                return $"Akun '{normalizedCode}' belum dikonfigurasi tipe buku bantunya.";
            }

            if (string.IsNullOrWhiteSpace(normalizedSubledgerCode))
            {
                return $"Akun '{normalizedCode}' wajib memakai buku bantu {requiredType}.";
            }

            if (!string.Equals(normalizedSubledgerType, requiredType, StringComparison.OrdinalIgnoreCase))
            {
                return $"Akun '{normalizedCode}' hanya menerima buku bantu {requiredType}.";
            }

            if (subledgerLookupByKey is null ||
                !subledgerLookupByKey.TryGetValue(BuildSubledgerLookupKey(requiredType, normalizedSubledgerCode), out var subledger))
            {
                return $"Buku bantu '{normalizedSubledgerCode}' tidak valid.";
            }

            if (!subledger.IsActive)
            {
                return $"Buku bantu '{normalizedSubledgerCode}' nonaktif.";
            }
        }

        var normalizedCostCenterCode = NormalizeCostCenterCode(line.CostCenterCode);
        if (account.RequiresCostCenter && string.IsNullOrWhiteSpace(normalizedCostCenterCode))
        {
            return $"Akun '{normalizedCode}' wajib memakai blok.";
        }

        if (!string.IsNullOrWhiteSpace(normalizedCostCenterCode))
        {
            if (costCenterLookupByCode is null ||
                !costCenterLookupByCode.TryGetValue(normalizedCostCenterCode, out var costCenter))
            {
                return account.RequiresCostCenter || line.BlockId.HasValue
                    ? $"Blok '{normalizedCostCenterCode}' tidak valid."
                    : null;
            }

            if (!costCenter.IsActive)
            {
                return $"Blok '{normalizedCostCenterCode}' nonaktif.";
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

    private static string NormalizeSubledgerType(string? subledgerType)
    {
        var normalized = (subledgerType ?? string.Empty).Trim().ToUpperInvariant();
        return normalized switch
        {
            "VENDOR" or "CUSTOMER" or "EMPLOYEE" => normalized,
            _ => string.Empty
        };
    }

    private static string NormalizeSubledgerCode(string? code)
    {
        return string.IsNullOrWhiteSpace(code)
            ? string.Empty
            : code.Trim().ToUpperInvariant();
    }

    private static string BuildSubledgerLookupKey(string subledgerType, string subledgerCode)
    {
        return $"{NormalizeSubledgerType(subledgerType)}|{NormalizeSubledgerCode(subledgerCode)}";
    }
}
