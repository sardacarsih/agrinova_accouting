using System;
using System.Windows;
using System.Windows.Controls;
using Accounting.Infrastructure.Logging;
using Accounting.Services;
using DevExpress.Xpf.Bars;
using DevExpress.Xpf.Core;
using DevExpress.Xpf.Core.Native;
using System.Collections.Generic;

namespace Accounting.Views.Components;

public partial class ThemeSkinsSelectorControl : UserControl
{
    private static readonly HashSet<string> CuratedThemeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        Theme.Win11LightName,
        Theme.Win11DarkName,
        Theme.Win10LightName,
        Theme.Win10DarkName,
        Theme.Office2019ColorfulName,
        Theme.Office2019WhiteName,
        Theme.Office2019DarkGrayName,
        Theme.Office2019BlackName,
        Theme.Office2019HighContrastName,
        Theme.VS2019BlueName,
        Theme.VS2019LightName,
        Theme.VS2019DarkName
    };

    private ThemeMode _lastReportedMode;
    private bool _isLoaded;

    public ThemeSkinsSelectorControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public event EventHandler<ThemeModeChangedEventArgs>? ThemeModeChanged;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_isLoaded)
        {
            return;
        }

        _isLoaded = true;
        ApplyThemeSelectorFilter();
        ApplicationThemeHelper.ApplicationThemeNameChanged += OnApplicationThemeNameChanged;
        _lastReportedMode = AppServices.ThemeCoordinator.CurrentThemeMode;
        UpdateThemePresentation(_lastReportedMode);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded)
        {
            return;
        }

        ApplicationThemeHelper.ApplicationThemeNameChanged -= OnApplicationThemeNameChanged;

        if (ThemeSelectorBehavior?.ThemesCollection is not null)
        {
            ThemeSelectorBehavior.ThemesCollection.Filter = null;
        }

        _isLoaded = false;
    }

    private void ApplyThemeSelectorFilter()
    {
        if (ThemeSelectorBehavior?.ThemesCollection is null)
        {
            return;
        }

        ThemeSelectorBehavior.ThemesCollection.Filter = FilterSupportedTheme;
        ThemeSelectorBehavior.ThemesCollection.Refresh();
    }

    private static bool FilterSupportedTheme(object item)
    {
        if (item is not ThemeViewModel themeViewModel || themeViewModel.Theme is not ITheme theme)
        {
            return false;
        }

        var activeThemeName = ApplicationThemeHelper.ApplicationThemeName;
        return theme.ShowInThemeSelector
            && !theme.IsTouchTheme
            && !theme.IsPaletteTheme
            && (CuratedThemeNames.Contains(theme.Name) ||
                string.Equals(theme.Name, activeThemeName, StringComparison.OrdinalIgnoreCase));
    }

    private void OnApplicationThemeNameChanged(object? sender, ApplicationThemeNameChangedEventArgs e)
    {
        var currentMode = AppServices.ThemeCoordinator.CurrentThemeMode;
        UpdateThemePresentation(currentMode);

        if (currentMode == _lastReportedMode)
        {
            return;
        }

        _lastReportedMode = currentMode;
        ThemeModeChanged?.Invoke(this, new ThemeModeChangedEventArgs(currentMode));
    }

    private void UpdateThemePresentation(ThemeMode mode)
    {
        var themeName = ApplicationThemeHelper.ApplicationThemeName;
        var displayName = ResolveThemeDisplayName(themeName, mode);

        CurrentThemeBadgeText.Text = displayName;
    }

    private static string ResolveThemeDisplayName(string? themeName, ThemeMode mode)
    {
        if (!string.IsNullOrWhiteSpace(themeName))
        {
            foreach (ITheme theme in Theme.Themes)
            {
                if (string.Equals(theme.Name, themeName, StringComparison.OrdinalIgnoreCase))
                {
                    return theme.DisplayName;
                }
            }
        }

        return mode switch
        {
            ThemeMode.Light => "Light skin",
            ThemeMode.Dark => "Dark skin",
            _ => "System mode"
        };
    }
}

public sealed class ThemeModeChangedEventArgs : EventArgs
{
    public ThemeModeChangedEventArgs(ThemeMode themeMode)
    {
        ThemeMode = themeMode;
    }

    public ThemeMode ThemeMode { get; }
}
