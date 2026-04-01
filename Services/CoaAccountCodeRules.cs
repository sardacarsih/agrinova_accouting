namespace Accounting.Services;

public static class CoaAccountCodeRules
{
    private static readonly Dictionary<string, string> AccountTypeByPrefix = new(StringComparer.OrdinalIgnoreCase)
    {
        ["20"] = "ASSET",
        ["80"] = "EXPENSE",
        ["81"] = "EXPENSE"
    };

    public static bool IsSegmentedAccountCode(string? accountCode)
    {
        var code = (accountCode ?? string.Empty).Trim().ToUpperInvariant();
        if (code.Length != 12 || code[2] != '.' || code[8] != '.')
        {
            return false;
        }

        if (!char.IsLetterOrDigit(code[0]) || !char.IsLetterOrDigit(code[1]))
        {
            return false;
        }

        for (var i = 3; i <= 7; i++)
        {
            if (!char.IsDigit(code[i]))
            {
                return false;
            }
        }

        for (var i = 9; i <= 11; i++)
        {
            if (!char.IsDigit(code[i]))
            {
                return false;
            }
        }

        return true;
    }

    public static bool TryDeriveAccountType(string? accountCode, out string accountType)
    {
        accountType = string.Empty;

        var code = (accountCode ?? string.Empty).Trim().ToUpperInvariant();
        if (!IsSegmentedAccountCode(code))
        {
            return false;
        }

        if (!AccountTypeByPrefix.TryGetValue(code[..2], out var mappedAccountType))
        {
            return false;
        }

        accountType = mappedAccountType;
        return true;
    }
}
