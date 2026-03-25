namespace Accounting.Services;

public sealed class UserAccessContext
{
    public long UserId { get; init; }

    public string Username { get; init; } = string.Empty;

    public long SelectedRoleId { get; init; }

    public string SelectedRoleCode { get; init; } = string.Empty;

    public string SelectedRoleName { get; init; } = string.Empty;

    public long SelectedCompanyId { get; init; }

    public string SelectedCompanyCode { get; init; } = string.Empty;

    public string SelectedCompanyName { get; init; } = string.Empty;

    public long SelectedLocationId { get; init; }

    public string SelectedLocationCode { get; init; } = string.Empty;

    public string SelectedLocationName { get; init; } = string.Empty;

    public long? DefaultCompanyId { get; init; }

    public long? DefaultLocationId { get; init; }

    public bool IsSuperRole { get; init; }

    public HashSet<string> RoleCodes { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> ModuleCodes { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> SubmoduleCodes { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> ActionCodes { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> ScopeCodes { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<long> CompanyIds { get; init; } = new();

    public HashSet<long> LocationIds { get; init; } = new();

    public HashSet<long> AllowedCompanyIds { get; init; } = new();

    public HashSet<long> AllowedLocationIds { get; init; } = new();

    public bool HasScope(string scopeCode)
    {
        return IsSuperRole ||
               ScopeCodes.Contains(scopeCode) ||
               ModuleCodes.Contains(scopeCode) ||
               SubmoduleCodes.Contains(scopeCode) ||
               ActionCodes.Contains(scopeCode);
    }

    public bool HasModule(string moduleCode)
    {
        return IsSuperRole || ModuleCodes.Contains(moduleCode);
    }

    public bool HasSubmodule(string moduleCode, string submoduleCode)
    {
        if (IsSuperRole)
        {
            return true;
        }

        var key = $"{moduleCode}.{submoduleCode}";
        return SubmoduleCodes.Contains(key);
    }

    public bool HasAction(string moduleCode, string submoduleCode, string actionCode)
    {
        if (IsSuperRole)
        {
            return true;
        }

        var key = $"{moduleCode}.{submoduleCode}.{actionCode}";
        return ActionCodes.Contains(key);
    }
}

public sealed class LoginAccessOptions
{
    public long UserId { get; init; }

    public string Username { get; init; } = string.Empty;

    public long? DefaultCompanyId { get; init; }

    public long? DefaultLocationId { get; init; }

    public List<ManagedRole> Roles { get; init; } = new();

    public List<ManagedCompany> Companies { get; init; } = new();

    public List<ManagedLocation> Locations { get; init; } = new();

    public Dictionary<long, HashSet<string>> ScopeCodesByRoleId { get; init; } = new();

    public Dictionary<long, HashSet<string>> ModuleCodesByRoleId { get; init; } = new();

    public Dictionary<long, HashSet<string>> SubmoduleCodesByRoleId { get; init; } = new();

    public Dictionary<long, HashSet<string>> ActionCodesByRoleId { get; init; } = new();

    public Dictionary<long, HashSet<long>> CompanyIdsByUserId { get; init; } = new();

    public Dictionary<long, HashSet<long>> LocationIdsByUserId { get; init; } = new();
}

public sealed class AccessOperationResult
{
    public AccessOperationResult(bool isSuccess, string message, long? entityId = null)
    {
        IsSuccess = isSuccess;
        Message = message;
        EntityId = entityId;
    }

    public bool IsSuccess { get; }

    public string Message { get; }

    public long? EntityId { get; }
}
