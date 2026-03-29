using DevExpress.Xpf.Core;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Accounting.Infrastructure.Logging;
using Accounting.Services;
using Accounting.ViewModels;
using Accounting.Views.Components;

namespace Accounting;

public partial class LoginWindow : ThemedWindow
{
    private readonly LoginViewModel _viewModel;
    private readonly IAccessControlService _accessControlService;

    public LoginWindow()
    {
        InitializeComponent();

        var appearance = AppServices.ThemeCoordinator.CurrentSettings;
        _accessControlService = AuthServiceFactory.CreateAccessControlService();

        _viewModel = new LoginViewModel(
            AuthServiceFactory.Create(),
            appearance.ThemeMode,
            OnAppearanceChanged,
            OnSignInSucceeded);
        DataContext = _viewModel;

        Loaded += (_, _) =>
        {
            UpdateAdaptiveLayout();
            UpdateWindowNavigationState();
            UpdateLoginSkinSummary();
            Keyboard.Focus(UsernameInput);
        };
    }

    private void Window_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateAdaptiveLayout();
    }

    private void Window_OnStateChanged(object sender, EventArgs e)
    {
        UpdateWindowNavigationState();
    }

    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            ToggleMaximizeRestore();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            e.Handled = true;
            return;
        }

        if (e.Key == Key.M && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            WindowState = WindowState.Minimized;
            e.Handled = true;
        }
    }

    private void MinimizeButton_OnClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeRestoreButton_OnClick(object sender, RoutedEventArgs e)
    {
        ToggleMaximizeRestore();
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void UsernameInput_OnLostFocus(object sender, RoutedEventArgs e)
    {
        _viewModel.MarkUsernameTouched();
    }

    private void UsernameInput_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(_viewModel.Password))
        {
            return;
        }

        FocusPasswordInput();
        e.Handled = true;
    }

    private void PasswordInput_OnLostFocus(object sender, RoutedEventArgs e)
    {
        _viewModel.MarkPasswordTouched();
    }

    private void FocusPasswordInput()
    {
        if (_viewModel.IsPasswordVisible)
        {
            PasswordVisibleInput.Focus();
            PasswordVisibleInput.SelectAll();
            return;
        }

        PasswordMaskedInput.Focus();
        PasswordMaskedInput.SelectAll();
    }

    private void UpdateAdaptiveLayout()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        _viewModel.UpdateLayout(ActualWidth, ActualHeight, dpi.DpiScaleX);
    }

    private void ToggleMaximizeRestore()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void UpdateWindowNavigationState()
    {
        if (MaximizeRestoreButton is null)
        {
            return;
        }

        var isMaximized = WindowState == WindowState.Maximized;
        MaximizeRestoreButton.Content = isMaximized ? "Restore" : "Maximize";
        MaximizeRestoreButton.ToolTip = isMaximized
            ? "Restore window (F11)"
            : "Maximize window (F11)";
    }

    private void OnAppearanceChanged(ThemeMode mode)
    {
        AppServices.ThemeCoordinator.ApplyTheme(mode);
    }

    private void ThemeSkinsSelectorControl_OnThemeModeChanged(object sender, ThemeModeChangedEventArgs e)
    {
        _viewModel.SyncThemeMode(e.ThemeMode);
        UpdateLoginSkinSummary();
        LoginThemeSelectorPopup.IsOpen = false;
    }

    private void LoginThemeSelectorButton_OnClick(object sender, RoutedEventArgs e)
    {
        LoginThemeSelectorPopup.IsOpen = !LoginThemeSelectorPopup.IsOpen;
    }

    private void LoginThemeSelectorPopup_OnClosed(object sender, EventArgs e)
    {
        UpdateLoginSkinSummary();
    }

    private void UpdateLoginSkinSummary()
    {
        if (LoginSkinSummaryText is null)
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
                    LoginSkinSummaryText.Text = theme.DisplayName;
                    return;
                }
            }
        }

        LoginSkinSummaryText.Text = AppServices.ThemeCoordinator.CurrentThemeMode switch
        {
            ThemeMode.Light => "Light skin",
            ThemeMode.Dark => "Dark skin",
            _ => "System mode"
        };
    }

    private async Task OnSignInSucceeded(string username)
    {
        var loginOptions = await _accessControlService.GetLoginAccessOptionsAsync(username);
        if (loginOptions is null || loginOptions.Roles.Count == 0)
        {
            _viewModel.SetFormError("Profil akses pengguna tidak dapat dimuat.");
            return;
        }

        if (loginOptions.Roles.Count != 1)
        {
            _viewModel.SetFormError("Akun ini harus memiliki tepat satu role aktif. Hubungi administrator.");
            return;
        }

        var selectorViewModel = new AccessSelectionViewModel(loginOptions);
        UserAccessContext? accessContext;

        if (selectorViewModel.HasMultipleChoices)
        {
            var selectionWindow = new AccessSelectionWindow(selectorViewModel)
            {
                Owner = this
            };

            var selectionResult = selectionWindow.ShowDialog();
            if (selectionResult != true || selectionWindow.SelectedSessionContext is null)
            {
                _viewModel.SetFormError("Pemilihan konteks akses dibatalkan.");
                return;
            }

            accessContext = selectionWindow.SelectedSessionContext;
        }
        else
        {
            if (!selectorViewModel.TryBuildSessionContext(out accessContext) || accessContext is null)
            {
                _viewModel.SetFormError(string.IsNullOrWhiteSpace(selectorViewModel.ErrorMessage)
                    ? "Konteks akses tidak dapat ditentukan."
                    : selectorViewModel.ErrorMessage);
                return;
            }
        }

        try
        {
            var workspace = new MainWindow(accessContext, _accessControlService);
            Application.Current.MainWindow = workspace;
            workspace.Show();
            Close();
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogError(
                nameof(LoginWindow),
                "OpenWorkspaceFailed",
                $"action=open_workspace username={username.Trim()}",
                ex);
            _viewModel.SetFormError($"Gagal membuka workspace: {ex.Message}");
        }
    }
}

