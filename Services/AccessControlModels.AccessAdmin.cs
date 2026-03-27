using Accounting.Infrastructure;

namespace Accounting.Services;

public sealed class ManagedAuditLog
{
    public long Id { get; set; }

    public string EntityType { get; set; } = string.Empty;

    public long EntityId { get; set; }

    public string Action { get; set; } = string.Empty;

    public string ActorUsername { get; set; } = string.Empty;

    public string Details { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}

public sealed class ManagedUser
{
    public long Id { get; set; }

    public string Username { get; set; } = string.Empty;

    public string FullName { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    public string RoleDisplay { get; set; } = "-";

    public string ModuleDisplay { get; set; } = "-";

    public long? DefaultCompanyId { get; set; }

    public long? DefaultLocationId { get; set; }

    public string DefaultCompanyDisplay { get; set; } = "-";

    public string DefaultLocationDisplay { get; set; } = "-";
}

public sealed class ManagedRole : ViewModelBase
{
    private long _id;
    private string _code = string.Empty;
    private string _name = string.Empty;
    private bool _isSuperRole;
    private bool _isActive;
    private int _assignedUserCount;
    private int _permissionCount;

    public long Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public string Code
    {
        get => _code;
        set => SetProperty(ref _code, value);
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public bool IsSuperRole
    {
        get => _isSuperRole;
        set => SetProperty(ref _isSuperRole, value);
    }

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public int AssignedUserCount
    {
        get => _assignedUserCount;
        set => SetProperty(ref _assignedUserCount, value);
    }

    public int PermissionCount
    {
        get => _permissionCount;
        set => SetProperty(ref _permissionCount, value);
    }

    public override string ToString()
    {
        if (string.IsNullOrWhiteSpace(Code))
        {
            return Name;
        }

        return string.IsNullOrWhiteSpace(Name) ? Code : $"{Code} - {Name}";
    }
}

public sealed class ManagedAccessScope
{
    public long Id { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string ModuleCode { get; set; } = string.Empty;

    public string ModuleName { get; set; } = string.Empty;

    public string SubmoduleCode { get; set; } = string.Empty;

    public string SubmoduleName { get; set; } = string.Empty;

    public string ActionCode { get; set; } = string.Empty;

    public bool IsActive { get; set; }
}

public sealed class ManagedCompany
{
    public long Id { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    public override string ToString()
    {
        if (string.IsNullOrWhiteSpace(Code))
        {
            return Name;
        }

        return string.IsNullOrWhiteSpace(Name) ? Code : $"{Code} - {Name}";
    }
}

public sealed class ManagedLocation
{
    public long Id { get; set; }

    public long CompanyId { get; set; }

    public string CompanyCode { get; set; } = string.Empty;

    public string CompanyName { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string LocationType { get; set; } = "OFFICE";

    public bool IsActive { get; set; }

    public override string ToString()
    {
        var locationText = string.IsNullOrWhiteSpace(Name) ? Code : $"{Code} - {Name}";
        return string.IsNullOrWhiteSpace(CompanyCode) ? locationText : $"{CompanyCode} - {locationText}";
    }
}

public sealed class UserManagementData
{
    public List<ManagedUser> Users { get; init; } = new();

    public List<ManagedRole> Roles { get; init; } = new();

    public List<ManagedAccessScope> AccessScopes { get; init; } = new();

    public List<ManagedCompany> Companies { get; init; } = new();

    public List<ManagedLocation> Locations { get; init; } = new();

    public Dictionary<long, HashSet<long>> UserRoleIdsByUserId { get; init; } = new();

    public Dictionary<long, HashSet<long>> RoleScopeIdsByRoleId { get; init; } = new();

    public Dictionary<long, HashSet<long>> UserCompanyIdsByUserId { get; init; } = new();

    public Dictionary<long, HashSet<long>> UserLocationIdsByUserId { get; init; } = new();

    public Dictionary<long, UserEffectiveAccessDetail> UserEffectiveAccessByUserId { get; init; } = new();

    public Dictionary<long, RoleAuditDetail> RoleAuditByRoleId { get; init; } = new();
}

public sealed class UserEffectiveAccessActionDetail
{
    public long ScopeId { get; init; }

    public string Label { get; init; } = string.Empty;

    public string ActionCode { get; init; } = string.Empty;

    public string GrantedByRole { get; init; } = string.Empty;
}

public sealed class UserEffectiveAccessSubmoduleDetail
{
    public string ModuleCode { get; init; } = string.Empty;

    public string ModuleName { get; init; } = string.Empty;

    public string SubmoduleCode { get; init; } = string.Empty;

    public string SubmoduleName { get; init; } = string.Empty;

    public List<UserEffectiveAccessActionDetail> Actions { get; init; } = new();
}

public sealed class UserEffectiveAccessModuleDetail
{
    public string ModuleCode { get; init; } = string.Empty;

    public string ModuleName { get; init; } = string.Empty;

    public List<UserEffectiveAccessSubmoduleDetail> Submodules { get; init; } = new();
}

public sealed class UserEffectiveAccessDetail
{
    public long UserId { get; init; }

    public long? RoleId { get; init; }

    public string RoleCode { get; init; } = string.Empty;

    public string RoleName { get; init; } = string.Empty;

    public bool IsSuperRole { get; init; }

    public List<long> CompanyIds { get; init; } = new();

    public List<string> CompanyLabels { get; init; } = new();

    public List<long> LocationIds { get; init; } = new();

    public List<string> LocationLabels { get; init; } = new();

    public List<UserEffectiveAccessModuleDetail> Modules { get; init; } = new();
}

public sealed class RoleAuditDetail
{
    public long RoleId { get; init; }

    public string RoleCode { get; init; } = string.Empty;

    public string RoleName { get; init; } = string.Empty;

    public bool IsSuperRole { get; init; }

    public List<long> PersistedScopeIds { get; init; } = new();

    public List<ManagedUser> AssignedUsers { get; init; } = new();
}
