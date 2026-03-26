using System.Windows;
using System.Windows.Media;
using Accounting.Services;
using Accounting.ViewModels;

namespace Accounting;

public partial class MainWindow : Window
{
    private readonly ThemeMode _selectedThemeMode;
    private readonly bool _isHighContrast;
    private readonly IAccessControlService _accessControlService;
    private bool _isSwitchingWorkContext;

    private MainWindowViewModel? _viewModel;

    public MainWindow(UserAccessContext accessContext, ThemeMode selectedThemeMode, bool isHighContrast, IAccessControlService accessControlService)
    {
        InitializeComponent();
        _selectedThemeMode = selectedThemeMode;
        _isHighContrast = isHighContrast;
        _accessControlService = accessControlService;

        var themeService = new ThemeService();
        themeService.ApplyTheme(selectedThemeMode, isHighContrast, animate: false);

        var shellViewModel = new MainShellViewModel(
            accessContext,
            ResolveEnvironmentName(),
            accessControlService,
            OnSignOutRequested,
            OnWorkContextSelectorRequested);
        _viewModel = new MainWindowViewModel(shellViewModel);
        DataContext = _viewModel;

        Loaded += (_, _) => UpdateAdaptiveLayout();
        Closed += (_, _) => _viewModel?.Dispose();
    }

    private static string ResolveEnvironmentName()
    {
        var env = Environment.GetEnvironmentVariable("AGRINOVA_ENVIRONMENT");
        return string.IsNullOrWhiteSpace(env) ? "PROD" : env.Trim().ToUpperInvariant();
    }

    private void MainWindow_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        UpdateAdaptiveLayout();
    }

    private void UpdateAdaptiveLayout()
    {
        if (_viewModel is null)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        _viewModel.UpdateLayout(ActualWidth, dpi.DpiScaleX);
    }

    private void OnSignOutRequested()
    {
        var loginWindow = new LoginWindow();
        Application.Current.MainWindow = loginWindow;
        loginWindow.Show();
        Close();
    }

    private void OnWorkContextSelectorRequested()
    {
        _ = OpenWorkContextSelectorAsync();
    }

    private async Task OpenWorkContextSelectorAsync()
    {
        if (_isSwitchingWorkContext)
        {
            return;
        }

        _isSwitchingWorkContext = true;
        try
        {
            var username = _viewModel?.Shell.CurrentUserDisplayName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(username))
            {
                MessageBox.Show(
                    "Username aktif tidak ditemukan. Silakan sign out lalu login ulang.",
                    "Konteks Kerja",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var loginOptions = await _accessControlService.GetLoginAccessOptionsAsync(username);
            if (loginOptions is null || loginOptions.Roles.Count == 0)
            {
                MessageBox.Show(
                    "Profil akses tidak dapat dimuat.",
                    "Konteks Kerja",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (loginOptions.Roles.Count != 1)
            {
                MessageBox.Show(
                    "Akun ini harus memiliki tepat satu role aktif untuk mengganti konteks kerja.",
                    "Konteks Kerja",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var selectorViewModel = new AccessSelectionViewModel(loginOptions);
            UserAccessContext? nextContext;
            if (selectorViewModel.HasMultipleChoices)
            {
                var selectionWindow = new AccessSelectionWindow(selectorViewModel)
                {
                    Owner = this
                };

                var selectionResult = selectionWindow.ShowDialog();
                if (selectionResult != true || selectionWindow.SelectedSessionContext is null)
                {
                    return;
                }

                nextContext = selectionWindow.SelectedSessionContext;
            }
            else
            {
                if (!selectorViewModel.TryBuildSessionContext(out nextContext) || nextContext is null)
                {
                    MessageBox.Show(
                        string.IsNullOrWhiteSpace(selectorViewModel.ErrorMessage)
                            ? "Konteks kerja tidak dapat ditentukan."
                            : selectorViewModel.ErrorMessage,
                        "Konteks Kerja",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }
            }

            var workspace = new MainWindow(nextContext, _selectedThemeMode, _isHighContrast, _accessControlService);
            Application.Current.MainWindow = workspace;
            workspace.Show();
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Gagal membuka form pilihan konteks kerja: {ex.Message}",
                "Konteks Kerja",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _isSwitchingWorkContext = false;
        }
    }
}

