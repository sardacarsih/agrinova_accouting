using System.Collections;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using Accounting.Infrastructure;
using Accounting.Infrastructure.Logging;
using Accounting.Services;

namespace Accounting.ViewModels;

public sealed class LoginViewModel : ViewModelBase, INotifyDataErrorInfo
{
    private static readonly Regex UsernameRegex = new(
        @"^[a-zA-Z0-9._-]{3,50}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    private readonly Dictionary<string, List<string>> _errors = new();
    private readonly IAuthService _authService;
    private readonly RelayCommand _signInCommand;
    private readonly Action<ThemeMode, bool>? _appearanceChanged;
    private readonly Func<string, Task>? _signInSucceeded;

    private string _username = string.Empty;
    private string _password = string.Empty;
    private bool _rememberMe = true;
    private bool _isPasswordVisible;
    private bool _isBusy;
    private string _formErrorMessage = string.Empty;
    private bool _isSubmitAttempted;
    private bool _isUsernameTouched;
    private bool _isPasswordTouched;
    private bool _isCompactLayout;
    private ThemeMode _selectedThemeMode;
    private bool _isHighContrast;

    private Thickness _shellMargin = new(40);
    private double _containerMaxWidth = 1720;
    private double _shellVerticalOffset = -12;
    private GridLength _brandingColumnWidth = new(1.08, GridUnitType.Star);
    private Thickness _brandingPanelMargin = new(0, 0, 28, 0);
    private Visibility _brandingVisibility = Visibility.Visible;
    private double _brandingMinHeight = 540;
    private double _brandingHeadlineFontSize = 34;
    private double _cardMinWidth = 440;
    private double _cardMaxWidth = 520;
    private Thickness _cardPadding = new(40);
    private double _titleFontSize = 30;
    private double _subtitleFontSize = 15;

    public LoginViewModel(
        IAuthService authService,
        ThemeMode selectedThemeMode,
        bool isHighContrast,
        Action<ThemeMode, bool>? appearanceChanged = null,
        Func<string, Task>? signInSucceeded = null)
    {
        _authService = authService;
        _selectedThemeMode = selectedThemeMode;
        _isHighContrast = isHighContrast;
        _appearanceChanged = appearanceChanged;
        _signInSucceeded = signInSucceeded;

        _signInCommand = new RelayCommand(async () => await SignInAsync(), CanSignIn);
        SignInCommand = _signInCommand;
        TogglePasswordVisibilityCommand = new RelayCommand(() => IsPasswordVisible = !IsPasswordVisible);
        ForgotPasswordCommand = new RelayCommand(() =>
        {
            FormErrorMessage = "Use your organization account recovery flow or contact support.";
            OnPropertyChanged(nameof(HasFormError));
        });
        SupportCommand = new RelayCommand(() =>
        {
            FormErrorMessage = "Support: support@company.com | +62-21-555-0142";
            OnPropertyChanged(nameof(HasFormError));
        });

        AppVersionText = $"v{typeof(LoginViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"}";
        CopyrightText = $"© {DateTime.Now.Year} Contoso Enterprise Systems";
    }

    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    public ICommand SignInCommand { get; }

    public ICommand TogglePasswordVisibilityCommand { get; }

    public ICommand ForgotPasswordCommand { get; }

    public ICommand SupportCommand { get; }

    public string AppVersionText { get; }

    public string CopyrightText { get; }

    public bool HasErrors => _errors.Count > 0;

    public Thickness ShellMargin
    {
        get => _shellMargin;
        private set => SetProperty(ref _shellMargin, value);
    }

    public double ContainerMaxWidth
    {
        get => _containerMaxWidth;
        private set => SetProperty(ref _containerMaxWidth, value);
    }

    public double ShellVerticalOffset
    {
        get => _shellVerticalOffset;
        private set => SetProperty(ref _shellVerticalOffset, value);
    }

    public GridLength BrandingColumnWidth
    {
        get => _brandingColumnWidth;
        private set => SetProperty(ref _brandingColumnWidth, value);
    }

    public Thickness BrandingPanelMargin
    {
        get => _brandingPanelMargin;
        private set => SetProperty(ref _brandingPanelMargin, value);
    }

    public Visibility BrandingVisibility
    {
        get => _brandingVisibility;
        private set => SetProperty(ref _brandingVisibility, value);
    }

    public double BrandingMinHeight
    {
        get => _brandingMinHeight;
        private set => SetProperty(ref _brandingMinHeight, value);
    }

    public double BrandingHeadlineFontSize
    {
        get => _brandingHeadlineFontSize;
        private set => SetProperty(ref _brandingHeadlineFontSize, value);
    }

    public double CardMinWidth
    {
        get => _cardMinWidth;
        private set => SetProperty(ref _cardMinWidth, value);
    }

    public double CardMaxWidth
    {
        get => _cardMaxWidth;
        private set => SetProperty(ref _cardMaxWidth, value);
    }

    public Thickness CardPadding
    {
        get => _cardPadding;
        private set => SetProperty(ref _cardPadding, value);
    }

    public double TitleFontSize
    {
        get => _titleFontSize;
        private set => SetProperty(ref _titleFontSize, value);
    }

    public double SubtitleFontSize
    {
        get => _subtitleFontSize;
        private set => SetProperty(ref _subtitleFontSize, value);
    }

    public string Username
    {
        get => _username;
        set
        {
            if (!SetProperty(ref _username, value))
            {
                return;
            }

            ValidateUsername();
            RefreshComputedState();
        }
    }

    public string Password
    {
        get => _password;
        set
        {
            if (!SetProperty(ref _password, value))
            {
                return;
            }

            ValidatePassword();
            RefreshComputedState();
        }
    }

    public bool RememberMe
    {
        get => _rememberMe;
        set => SetProperty(ref _rememberMe, value);
    }

    public bool IsPasswordVisible
    {
        get => _isPasswordVisible;
        set => SetProperty(ref _isPasswordVisible, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
            {
                return;
            }

            OnPropertyChanged(nameof(SignInButtonText));
            _signInCommand.RaiseCanExecuteChanged();
        }
    }

    public string FormErrorMessage
    {
        get => _formErrorMessage;
        private set
        {
            if (SetProperty(ref _formErrorMessage, value))
            {
                OnPropertyChanged(nameof(HasFormError));
            }
        }
    }

    public bool HasFormError => !string.IsNullOrWhiteSpace(FormErrorMessage);

    public bool IsCompactLayout
    {
        get => _isCompactLayout;
        private set => SetProperty(ref _isCompactLayout, value);
    }

    public ThemeMode SelectedThemeMode
    {
        get => _selectedThemeMode;
        set
        {
            if (!SetProperty(ref _selectedThemeMode, value))
            {
                return;
            }

            _appearanceChanged?.Invoke(SelectedThemeMode, IsHighContrast);
        }
    }

    public bool IsHighContrast
    {
        get => _isHighContrast;
        set
        {
            if (!SetProperty(ref _isHighContrast, value))
            {
                return;
            }

            _appearanceChanged?.Invoke(SelectedThemeMode, IsHighContrast);
        }
    }

    public string SignInButtonText => IsBusy ? "Signing in..." : "Sign In";

    public string? UsernameError => GetFirstError(nameof(Username));

    public string? PasswordError => GetFirstError(nameof(Password));

    public bool HasUsernameError => ShouldShowFieldError(_isUsernameTouched) && !string.IsNullOrWhiteSpace(UsernameError);

    public bool HasPasswordError => ShouldShowFieldError(_isPasswordTouched) && !string.IsNullOrWhiteSpace(PasswordError);

    public IEnumerable GetErrors(string? propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName) || !_errors.TryGetValue(propertyName, out var propertyErrors))
        {
            return Array.Empty<string>();
        }

        return propertyErrors;
    }

    public void MarkUsernameTouched()
    {
        _isUsernameTouched = true;
        OnPropertyChanged(nameof(HasUsernameError));
    }

    public void MarkPasswordTouched()
    {
        _isPasswordTouched = true;
        OnPropertyChanged(nameof(HasPasswordError));
    }

    public void UpdateLayout(double windowWidth, double windowHeight, double dpiScale)
    {
        var width = Math.Max(windowWidth, 1024);
        var height = Math.Max(windowHeight, 640);
        var aspectRatio = width / height;
        var dpiAdjustment = Math.Clamp((dpiScale - 1.0) * 14.0, 0.0, 12.0);

        if (width >= 2560 || aspectRatio >= 2.2)
        {
            IsCompactLayout = false;
            BrandingVisibility = Visibility.Visible;
            BrandingColumnWidth = new GridLength(1.24, GridUnitType.Star);
            BrandingPanelMargin = new Thickness(0, 0, 36, 0);
            BrandingMinHeight = 600;
            BrandingHeadlineFontSize = 36;
            ContainerMaxWidth = 2040;
            ShellMargin = new Thickness(52, 36, 52, 36);
            ShellVerticalOffset = -20;
            CardMinWidth = 448;
            CardMaxWidth = 540;
            CardPadding = new Thickness(42 - dpiAdjustment, 40 - dpiAdjustment, 42 - dpiAdjustment, 40 - dpiAdjustment);
            TitleFontSize = 31;
            SubtitleFontSize = 15.5;
            return;
        }

        if (width >= 1920)
        {
            IsCompactLayout = false;
            BrandingVisibility = Visibility.Visible;
            BrandingColumnWidth = new GridLength(1.12, GridUnitType.Star);
            BrandingPanelMargin = new Thickness(0, 0, 28, 0);
            BrandingMinHeight = 560;
            BrandingHeadlineFontSize = 34;
            ContainerMaxWidth = 1720;
            ShellMargin = new Thickness(40, 32, 40, 32);
            ShellVerticalOffset = -14;
            CardMinWidth = 440;
            CardMaxWidth = 520;
            CardPadding = new Thickness(40 - dpiAdjustment, 38 - dpiAdjustment, 40 - dpiAdjustment, 38 - dpiAdjustment);
            TitleFontSize = 30;
            SubtitleFontSize = 15;
            return;
        }

        if (width >= 1400 && height >= 760)
        {
            IsCompactLayout = false;
            BrandingVisibility = Visibility.Visible;
            BrandingColumnWidth = new GridLength(0.96, GridUnitType.Star);
            BrandingPanelMargin = new Thickness(0, 0, 22, 0);
            BrandingMinHeight = 520;
            BrandingHeadlineFontSize = 31;
            ContainerMaxWidth = 1380;
            ShellMargin = new Thickness(28, 24, 28, 24);
            ShellVerticalOffset = -10;
            CardMinWidth = 420;
            CardMaxWidth = 500;
            CardPadding = new Thickness(34 - dpiAdjustment, 32 - dpiAdjustment, 34 - dpiAdjustment, 32 - dpiAdjustment);
            TitleFontSize = 28;
            SubtitleFontSize = 14.5;
            return;
        }

        IsCompactLayout = true;
        BrandingVisibility = Visibility.Collapsed;
        BrandingColumnWidth = new GridLength(0, GridUnitType.Pixel);
        BrandingPanelMargin = new Thickness(0);
        BrandingMinHeight = 0;
        BrandingHeadlineFontSize = 30;
        ContainerMaxWidth = 960;
        ShellMargin = new Thickness(20, 16, 20, 16);
        ShellVerticalOffset = -6;
        CardMinWidth = 392;
        CardMaxWidth = 500;
        CardPadding = new Thickness(28 - dpiAdjustment, 26 - dpiAdjustment, 28 - dpiAdjustment, 26 - dpiAdjustment);
        TitleFontSize = 27;
        SubtitleFontSize = 14;
    }

    private bool CanSignIn()
    {
        return !IsBusy;
    }

    private async Task SignInAsync()
    {
        _isSubmitAttempted = true;
        _isUsernameTouched = true;
        _isPasswordTouched = true;

        ValidateUsername();
        ValidatePassword();
        RefreshComputedState();

        if (HasErrors)
        {
            FormErrorMessage = "Please correct the highlighted fields and try again.";
            return;
        }

        FormErrorMessage = string.Empty;
        IsBusy = true;

        try
        {
            var result = await _authService.SignInAsync(Username, Password, RememberMe);
            if (!result.IsSuccess)
            {
                FormErrorMessage = result.ErrorMessage ?? "Authentication failed.";
                return;
            }

            FormErrorMessage = string.Empty;
            if (_signInSucceeded is not null)
            {
                await _signInSucceeded.Invoke(Username.Trim());
            }
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogError(
                nameof(LoginViewModel),
                "SignInUnexpectedError",
                $"action=sign_in username={Username.Trim()}",
                ex);
            FormErrorMessage = "Unable to reach authentication service. Please try again.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool ShouldShowFieldError(bool touched)
    {
        return touched || _isSubmitAttempted;
    }

    private void RefreshComputedState()
    {
        OnPropertyChanged(nameof(UsernameError));
        OnPropertyChanged(nameof(PasswordError));
        OnPropertyChanged(nameof(HasUsernameError));
        OnPropertyChanged(nameof(HasPasswordError));
        _signInCommand.RaiseCanExecuteChanged();
    }

    private string? GetFirstError(string propertyName)
    {
        return _errors.TryGetValue(propertyName, out var propertyErrors) ? propertyErrors.FirstOrDefault() : null;
    }

    private void ValidateUsername()
    {
        var errors = new List<string>();
        var normalized = Username.Trim();

        if (string.IsNullOrWhiteSpace(normalized))
        {
            errors.Add("Username is required.");
        }
        else if (!UsernameRegex.IsMatch(normalized))
        {
            errors.Add("Username must be 3-50 chars (letters, numbers, dot, underscore, hyphen).");
        }

        SetErrors(nameof(Username), errors);
    }

    private void ValidatePassword()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Password))
        {
            errors.Add("Password is required.");
        }
        else if (Password.Length < 8)
        {
            errors.Add("Password must be at least 8 characters.");
        }

        SetErrors(nameof(Password), errors);
    }

    private void SetErrors(string propertyName, List<string> propertyErrors)
    {
        if (propertyErrors.Count == 0)
        {
            if (_errors.Remove(propertyName))
            {
                ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(propertyName));
            }

            return;
        }

        _errors[propertyName] = propertyErrors;
        ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(propertyName));
    }

    public void SetFormError(string message)
    {
        FormErrorMessage = message;
    }
}

