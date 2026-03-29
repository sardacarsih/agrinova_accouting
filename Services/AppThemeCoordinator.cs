using DevExpress.Xpf.Core;
using Accounting.Infrastructure.Logging;

namespace Accounting.Services;

public sealed class AppThemeCoordinator : IDisposable
{
    private static readonly object SystemThemeSync = new();
    private static Theme? _registeredSystemTheme;
    private static string? _registeredSystemThemeName;

    private readonly AppearancePreferencesService _appearancePreferencesService = new();
    private readonly DevExpressThemeResourceBridge _themeResourceBridge = new();

    private UserAppearanceSettings _currentSettings = new();
    private bool _isDisposed;
    private bool _isApplyingTheme;

    public ThemeMode CurrentThemeMode => _currentSettings.ThemeMode;

    public UserAppearanceSettings CurrentSettings => new()
    {
        ThemeMode = _currentSettings.ThemeMode,
        PreferredThemeName = _currentSettings.PreferredThemeName
    };

    public UserAppearanceSettings Initialize()
    {
        ThrowIfDisposed();

        _themeResourceBridge.Initialize();
        ApplicationThemeHelper.ApplicationThemeNameChanged += OnApplicationThemeNameChanged;
        _currentSettings = _appearancePreferencesService.Load();
        ApplyThemeCore(_currentSettings.ThemeMode, _currentSettings.PreferredThemeName, persist: false);
        return CurrentSettings;
    }

    public void ApplyTheme(ThemeMode mode)
    {
        ThrowIfDisposed();
        ApplyThemeCore(mode, preferredThemeName: null, persist: true);
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _themeResourceBridge.Dispose();
        ApplicationThemeHelper.ApplicationThemeNameChanged -= OnApplicationThemeNameChanged;
        _isDisposed = true;
    }

    private void ApplyThemeCore(ThemeMode mode, string? preferredThemeName, bool persist)
    {
        _currentSettings = new UserAppearanceSettings
        {
            ThemeMode = mode,
            PreferredThemeName = ResolvePersistedThemeName(mode, preferredThemeName)
        };

        _isApplyingTheme = true;
        try
        {
            ApplicationThemeHelper.ApplicationThemeName = _currentSettings.PreferredThemeName;
            _themeResourceBridge.ApplyAliases();
        }
        finally
        {
            _isApplyingTheme = false;
        }

        if (persist)
        {
            _appearancePreferencesService.Save(_currentSettings);
        }
    }

    private static string ResolveDevExpressThemeName(ThemeMode mode)
    {
        return mode switch
        {
            ThemeMode.Light => Theme.Win11LightName,
            ThemeMode.Dark => Theme.Win11DarkName,
            _ => EnsureSystemThemeRegistered()
        };
    }

    private static string ResolvePersistedThemeName(ThemeMode mode, string? preferredThemeName)
    {
        if (!string.IsNullOrWhiteSpace(preferredThemeName))
        {
            return preferredThemeName;
        }

        return ResolveDevExpressThemeName(mode);
    }

    private static string EnsureSystemThemeRegistered()
    {
        if (!string.IsNullOrWhiteSpace(_registeredSystemThemeName))
        {
            return _registeredSystemThemeName;
        }

        lock (SystemThemeSync)
        {
            if (!string.IsNullOrWhiteSpace(_registeredSystemThemeName))
            {
                return _registeredSystemThemeName;
            }

            try
            {
                _registeredSystemTheme = Theme.CreateTheme(new Win10Palette(true));
                Theme.RegisterTheme(_registeredSystemTheme);
                _registeredSystemThemeName = _registeredSystemTheme.Name;
            }
            catch (Exception ex)
            {
                _registeredSystemThemeName = Theme.Win10SystemColorsName;
                AppServices.Logger.LogWarning(
                    nameof(AppThemeCoordinator),
                    "RegisterSystemThemeFallback",
                    $"action=register_system_theme status=fallback theme={_registeredSystemThemeName}",
                    ex);
            }

            return _registeredSystemThemeName;
        }
    }

    private void OnApplicationThemeNameChanged(object? sender, ApplicationThemeNameChangedEventArgs e)
    {
        if (_isDisposed || _isApplyingTheme)
        {
            return;
        }

        var mappedMode = MapThemeNameToMode(e.ThemeName);
        if (mappedMode == _currentSettings.ThemeMode)
        {
            return;
        }

        _currentSettings = new UserAppearanceSettings
        {
            ThemeMode = mappedMode,
            PreferredThemeName = e.ThemeName
        };

        _themeResourceBridge.ApplyAliases();
        _appearancePreferencesService.Save(_currentSettings);
    }

    private static ThemeMode MapThemeNameToMode(string? themeName)
    {
        if (string.IsNullOrWhiteSpace(themeName))
        {
            return ThemeMode.System;
        }

        if (string.Equals(themeName, Theme.Win11LightName, StringComparison.OrdinalIgnoreCase))
        {
            return ThemeMode.Light;
        }

        if (string.Equals(themeName, Theme.Win11DarkName, StringComparison.OrdinalIgnoreCase))
        {
            return ThemeMode.Dark;
        }

        if (string.Equals(themeName, Theme.Win10SystemColorsName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(themeName, Theme.Win10SystemName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(themeName, Theme.Win11SystemName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(themeName, _registeredSystemThemeName, StringComparison.OrdinalIgnoreCase)
            || themeName.StartsWith("Windows10", StringComparison.OrdinalIgnoreCase))
        {
            return ThemeMode.System;
        }

        return themeName.Contains("Dark", StringComparison.OrdinalIgnoreCase)
            ? ThemeMode.Dark
            : ThemeMode.Light;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }
}
