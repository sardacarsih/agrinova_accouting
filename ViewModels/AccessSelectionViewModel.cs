using System.Collections.ObjectModel;
using System.Globalization;
using Accounting.Infrastructure;
using Accounting.Services;

namespace Accounting.ViewModels;

public sealed class AccessSelectionViewModel : ViewModelBase
{
    private readonly LoginAccessOptions _options;
    private readonly ManagedRole? _resolvedRole;
    private readonly HashSet<long> _allowedCompanyIds;
    private readonly HashSet<long> _allowedLocationIds;

    private ManagedCompany? _selectedCompany;
    private ManagedLocation? _selectedLocation;
    private string _errorMessage = string.Empty;

    public AccessSelectionViewModel(LoginAccessOptions options)
    {
        _options = options;
        _allowedCompanyIds = options.CompanyIdsByUserId.TryGetValue(options.UserId, out var companyIds)
            ? new HashSet<long>(companyIds)
            : new HashSet<long>();
        _allowedLocationIds = options.LocationIdsByUserId.TryGetValue(options.UserId, out var locationIds)
            ? new HashSet<long>(locationIds)
            : new HashSet<long>();

        var roleOptions = options.Roles.OrderBy(x => x.Code).ToList();
        _resolvedRole = roleOptions.Count == 1 ? roleOptions[0] : null;

        CompanyOptions = new ObservableCollection<ManagedCompany>();
        LocationOptions = new ObservableCollection<ManagedLocation>();

        RefreshCompanyOptions();
        HasMultipleChoices = CompanyOptions.Count > 1 || LocationOptions.Count > 1;
    }

    public ObservableCollection<ManagedCompany> CompanyOptions { get; }

    public ObservableCollection<ManagedLocation> LocationOptions { get; }

    public bool HasMultipleChoices { get; }

    public bool IsCompanyMulti => CompanyOptions.Count > 1;

    public bool IsLocationMulti => LocationOptions.Count > 1;

    public string SelectionHint
    {
        get
        {
            if (_resolvedRole is null)
            {
                return "Akun ini harus memiliki tepat satu role aktif sebelum konteks kerja dapat diganti.";
            }

            if (CompanyOptions.Count == 0)
            {
                return "Akun ini belum memiliki company aktif yang bisa dipakai.";
            }

            if (SelectedCompany is null)
            {
                return "Pilih company untuk melihat lokasi yang tersedia.";
            }

            if (LocationOptions.Count == 0)
            {
                return string.Format(
                    CultureInfo.CurrentCulture,
                    "Tidak ada lokasi aktif yang tersedia untuk company {0}.",
                    SelectedCompany.Code);
            }

            var lockedParts = new List<string>();
            if (!IsCompanyMulti)
            {
                lockedParts.Add("company akses Anda hanya satu");
            }

            if (!IsLocationMulti)
            {
                lockedParts.Add("lokasi untuk company ini hanya satu");
            }

            return lockedParts.Count == 0
                ? "Pilih company dan lokasi yang tersedia untuk melanjutkan."
                : $"Sebagian konteks terkunci karena {string.Join(" dan ", lockedParts)}.";
        }
    }

    public ManagedCompany? SelectedCompany
    {
        get => _selectedCompany;
        set
        {
            if (!SetProperty(ref _selectedCompany, value))
            {
                return;
            }

            RefreshLocationOptions();
        }
    }

    public ManagedLocation? SelectedLocation
    {
        get => _selectedLocation;
        set => SetProperty(ref _selectedLocation, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public bool TryBuildSessionContext(out UserAccessContext? accessContext)
    {
        accessContext = null;
        ErrorMessage = string.Empty;

        if (_resolvedRole is null)
        {
            ErrorMessage = "Akun ini harus memiliki tepat satu role aktif.";
            return false;
        }

        if (SelectedCompany is null)
        {
            ErrorMessage = "Tidak ada perusahaan yang tersedia untuk akun ini.";
            return false;
        }

        if (SelectedLocation is null)
        {
            ErrorMessage = "Tidak ada lokasi yang tersedia untuk perusahaan terpilih.";
            return false;
        }

        if (!_options.ScopeCodesByRoleId.TryGetValue(_resolvedRole.Id, out var scopeCodes))
        {
            scopeCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        if (!_options.ModuleCodesByRoleId.TryGetValue(_resolvedRole.Id, out var moduleCodes))
        {
            moduleCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        if (!_options.SubmoduleCodesByRoleId.TryGetValue(_resolvedRole.Id, out var submoduleCodes))
        {
            submoduleCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        if (!_options.ActionCodesByRoleId.TryGetValue(_resolvedRole.Id, out var actionCodes))
        {
            actionCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        accessContext = new UserAccessContext
        {
            UserId = _options.UserId,
            Username = _options.Username,
            SelectedRoleId = _resolvedRole.Id,
            SelectedRoleCode = _resolvedRole.Code,
            SelectedRoleName = _resolvedRole.Name,
            SelectedCompanyId = SelectedCompany.Id,
            SelectedCompanyCode = SelectedCompany.Code,
            SelectedCompanyName = SelectedCompany.Name,
            SelectedLocationId = SelectedLocation.Id,
            SelectedLocationCode = SelectedLocation.Code,
            SelectedLocationName = SelectedLocation.Name,
            IsSuperRole = _resolvedRole.IsSuperRole,
            RoleCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _resolvedRole.Code },
            ModuleCodes = new HashSet<string>(moduleCodes, StringComparer.OrdinalIgnoreCase),
            SubmoduleCodes = new HashSet<string>(submoduleCodes, StringComparer.OrdinalIgnoreCase),
            ActionCodes = new HashSet<string>(actionCodes, StringComparer.OrdinalIgnoreCase),
            ScopeCodes = new HashSet<string>(scopeCodes, StringComparer.OrdinalIgnoreCase),
            CompanyIds = new HashSet<long>(_allowedCompanyIds),
            LocationIds = new HashSet<long>(_allowedLocationIds),
            AllowedCompanyIds = new HashSet<long>(_allowedCompanyIds),
            AllowedLocationIds = new HashSet<long>(_allowedLocationIds),
            DefaultCompanyId = _options.DefaultCompanyId,
            DefaultLocationId = _options.DefaultLocationId
        };

        return true;
    }

    private void RefreshCompanyOptions()
    {
        CompanyOptions.Clear();
        LocationOptions.Clear();
        SelectedCompany = null;
        SelectedLocation = null;

        if (_resolvedRole is null)
        {
            NotifySelectionStateChanged();
            return;
        }

        foreach (var company in _options.Companies
                     .Where(x => _allowedCompanyIds.Contains(x.Id))
                     .OrderBy(x => x.Code))
        {
            CompanyOptions.Add(company);
        }

        OnPropertyChanged(nameof(IsCompanyMulti));
        OnPropertyChanged(nameof(SelectionHint));
        if (_options.DefaultCompanyId.HasValue)
        {
            SelectedCompany = CompanyOptions.FirstOrDefault(x => x.Id == _options.DefaultCompanyId.Value);
        }

        SelectedCompany ??= CompanyOptions.FirstOrDefault();
    }

    private void RefreshLocationOptions()
    {
        LocationOptions.Clear();
        SelectedLocation = null;

        if (_resolvedRole is null || SelectedCompany is null)
        {
            NotifySelectionStateChanged();
            return;
        }

        foreach (var location in _options.Locations
                     .Where(x => _allowedLocationIds.Contains(x.Id) && x.CompanyId == SelectedCompany.Id)
                     .OrderBy(x => x.Code))
        {
            LocationOptions.Add(location);
        }

        NotifySelectionStateChanged();
        if (_options.DefaultLocationId.HasValue)
        {
            SelectedLocation = LocationOptions.FirstOrDefault(x => x.Id == _options.DefaultLocationId.Value);
        }

        SelectedLocation ??= LocationOptions.FirstOrDefault();
    }

    private void NotifySelectionStateChanged()
    {
        OnPropertyChanged(nameof(IsCompanyMulti));
        OnPropertyChanged(nameof(IsLocationMulti));
        OnPropertyChanged(nameof(SelectionHint));
    }
}
