using DevExpress.Xpf.Core;
using System.Windows;
using System.Windows.Controls;

namespace Accounting.Views.Components;

public partial class HeaderBarControl : UserControl
{
    public HeaderBarControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplicationThemeHelper.ApplicationThemeNameChanged += OnApplicationThemeNameChanged;
        UpdateThemeSummary();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ApplicationThemeHelper.ApplicationThemeNameChanged -= OnApplicationThemeNameChanged;
    }

    private void HeaderThemeSelectorButton_OnClick(object sender, RoutedEventArgs e)
    {
        HeaderThemeSelectorPopup.IsOpen = !HeaderThemeSelectorPopup.IsOpen;
    }

    private void HeaderThemeSelectorPopup_OnClosed(object sender, EventArgs e)
    {
        UpdateThemeSummary();
    }

    private void ThemeSkinsSelectorControl_OnThemeModeChanged(object sender, ThemeModeChangedEventArgs e)
    {
        UpdateThemeSummary();
        HeaderThemeSelectorPopup.IsOpen = false;
    }

    private void OnApplicationThemeNameChanged(object? sender, ApplicationThemeNameChangedEventArgs e)
    {
        UpdateThemeSummary();
    }

    private void UpdateThemeSummary()
    {
        if (HeaderThemeSelectorButton is null)
        {
            return;
        }

        var themeName = ApplicationThemeHelper.ApplicationThemeName;
        if (!string.IsNullOrWhiteSpace(themeName))
        {
            foreach (DevExpress.Xpf.Core.Native.ITheme theme in Theme.Themes)
            {
                if (string.Equals(theme.Name, themeName, StringComparison.OrdinalIgnoreCase))
                {
                    HeaderThemeSelectorButton.ToolTip = $"Skin: {theme.DisplayName}";
                    return;
                }
            }
        }

        HeaderThemeSelectorButton.ToolTip = "Skin";
    }
}
