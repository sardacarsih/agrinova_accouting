using System.Windows;
using System.Windows.Media;
using DevExpress.Xpf.Core;
using DevExpress.Xpf.Utils.Themes;

namespace Accounting.Services;

public sealed class DevExpressThemeResourceBridge : IDisposable
{
    private bool _isDisposed;
    private bool _isInitialized;

    public void Initialize()
    {
        ThrowIfDisposed();
        if (_isInitialized)
        {
            return;
        }

        ApplicationThemeHelper.ApplicationThemeNameChanged += OnApplicationThemeNameChanged;
        ThemeManager.ApplicationThemeChanged += OnApplicationThemeChanged;
        _isInitialized = true;
        ApplyAliases();
    }

    public void ApplyAliases()
    {
        ThrowIfDisposed();

        var application = Application.Current;
        if (application?.Resources is null)
        {
            return;
        }

        var themeName = ApplicationThemeHelper.ApplicationThemeName;
        if (string.IsNullOrWhiteSpace(themeName))
        {
            return;
        }

        var palette = BuildPalette(application, themeName);
        var resources = application.Resources;

        SetColorResource(resources, "Color.Background", palette.Background);
        SetColorResource(resources, "Color.Surface", palette.Surface);
        SetColorResource(resources, "Color.SurfaceMuted", palette.SurfaceMuted);
        SetColorResource(resources, "Color.SurfaceAlt", palette.SurfaceAlt);
        SetColorResource(resources, "Color.Primary", palette.Primary);
        SetColorResource(resources, "Color.PrimaryDark", palette.PrimaryDark);
        SetColorResource(resources, "Color.PrimaryLight", palette.PrimaryLight);
        SetColorResource(resources, "Color.PrimarySubtle", palette.PrimarySubtle);
        SetColorResource(resources, "Color.TextPrimary", palette.TextPrimary);
        SetColorResource(resources, "Color.TextSecondary", palette.TextSecondary);
        SetColorResource(resources, "Color.TextMuted", palette.TextMuted);
        SetColorResource(resources, "Color.Border", palette.Border);
        SetColorResource(resources, "Color.BorderSoft", palette.BorderSoft);
        SetColorResource(resources, "Color.BorderStrong", palette.BorderStrong);
        SetColorResource(resources, "Color.Focus", palette.Focus);
        SetColorResource(resources, "Color.Error", palette.Error);
        SetColorResource(resources, "Color.ErrorSubtle", palette.ErrorSubtle);
        SetColorResource(resources, "Color.Success", palette.Success);
        SetColorResource(resources, "Color.Warning", palette.Warning);
        SetColorResource(resources, "Color.WarningSubtle", palette.WarningSubtle);
        SetColorResource(resources, "Color.Info", palette.Info);
        SetColorResource(resources, "Color.InfoSubtle", palette.InfoSubtle);
        SetColorResource(resources, "Color.DisabledBackground", palette.DisabledBackground);
        SetColorResource(resources, "Color.DisabledForeground", palette.DisabledForeground);
        SetColorResource(resources, "Color.Selection", palette.Selection);
        SetColorResource(resources, "Color.SelectionBorder", palette.SelectionBorder);
        SetColorResource(resources, "Color.Overlay", palette.Overlay);
        SetColorResource(resources, "Color.CardShadow", palette.CardShadow);

        SetBrushColor(resources, "Brush.Background", palette.Background);
        SetBrushColor(resources, "Brush.Surface", palette.Surface);
        SetBrushColor(resources, "Brush.SurfaceMuted", palette.SurfaceMuted);
        SetBrushColor(resources, "Brush.SurfaceAlt", palette.SurfaceAlt);
        SetBrushColor(resources, "Brush.Primary", palette.Primary);
        SetBrushColor(resources, "Brush.PrimaryDark", palette.PrimaryDark);
        SetBrushColor(resources, "Brush.PrimaryLight", palette.PrimaryLight);
        SetBrushColor(resources, "Brush.PrimarySubtle", palette.PrimarySubtle);
        SetBrushColor(resources, "Brush.TextPrimary", palette.TextPrimary);
        SetBrushColor(resources, "Brush.TextSecondary", palette.TextSecondary);
        SetBrushColor(resources, "Brush.TextMuted", palette.TextMuted);
        SetBrushColor(resources, "Brush.Border", palette.Border);
        SetBrushColor(resources, "Brush.BorderSoft", palette.BorderSoft);
        SetBrushColor(resources, "Brush.BorderStrong", palette.BorderStrong);
        SetBrushColor(resources, "Brush.Focus", palette.Focus);
        SetBrushColor(resources, "Brush.Error", palette.Error);
        SetBrushColor(resources, "Brush.ErrorSubtle", palette.ErrorSubtle);
        SetBrushColor(resources, "Brush.Success", palette.Success);
        SetBrushColor(resources, "Brush.Warning", palette.Warning);
        SetBrushColor(resources, "Brush.WarningSubtle", palette.WarningSubtle);
        SetBrushColor(resources, "Brush.Info", palette.Info);
        SetBrushColor(resources, "Brush.InfoSubtle", palette.InfoSubtle);
        SetBrushColor(resources, "Brush.DisabledBackground", palette.DisabledBackground);
        SetBrushColor(resources, "Brush.DisabledForeground", palette.DisabledForeground);
        SetBrushColor(resources, "Brush.Selection", palette.Selection);
        SetBrushColor(resources, "Brush.SelectionBorder", palette.SelectionBorder);
        SetBrushColor(resources, "Brush.Overlay", palette.Overlay);

        SetGradient(resources, "Brush.WindowGradient", palette.WindowGradientTop, palette.WindowGradientMiddle, palette.WindowGradientBottom);
        SetGradient(resources, "Brush.BrandingGradient", palette.BrandingGradientTop, palette.BrandingGradientMiddle, palette.BrandingGradientBottom);
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        if (_isInitialized)
        {
            ApplicationThemeHelper.ApplicationThemeNameChanged -= OnApplicationThemeNameChanged;
            ThemeManager.ApplicationThemeChanged -= OnApplicationThemeChanged;
        }

        _isDisposed = true;
    }

    private void OnApplicationThemeNameChanged(object? sender, ApplicationThemeNameChangedEventArgs e)
    {
        ApplyAliases();
    }

    private void OnApplicationThemeChanged(object sender, ThemeChangedRoutedEventArgs e)
    {
        ApplyAliases();
    }

    private static ThemeAliasPalette BuildPalette(Application application, string themeName)
    {
        var resources = application.Resources;
        var fallbackBackground = GetExistingColor(resources, "Color.Background", Color.FromRgb(0xF1, 0xF4, 0xF8));
        var fallbackSurface = GetExistingColor(resources, "Color.Surface", Colors.White);
        var fallbackForeground = GetExistingColor(resources, "Color.TextPrimary", Color.FromRgb(0x1F, 0x29, 0x37));

        var background = ResolveThemeColor(application, themeName, fallbackBackground, "Window.Background", "Control.Background");
        var surface = ResolveThemeColor(application, themeName, fallbackSurface, "Control.Background", "Button.Background", "Editor.Background");
        var textPrimary = ResolveThemeColor(application, themeName, fallbackForeground, "Foreground", "Control.Foreground", "Editor.Foreground");
        var border = ResolveThemeColor(application, themeName, Mix(surface, textPrimary, 0.16), "Border", "Control.NeutralBackground", "Delimiter");
        var primary = ResolveThemeColor(application, themeName, Color.FromRgb(0x0E, 0x59, 0x6A), "Accent", "SelectionBackground");
        var focus = ResolveThemeColor(application, themeName, primary, "Focused", "Accent");
        var selection = ResolveThemeColor(application, themeName, Mix(surface, primary, 0.18), "SelectionBackground", "Control.SelectionBackground", "Accent");
        var selectionBorder = ResolveThemeColor(application, themeName, primary, "SelectionBorder", "Accent", "Focused");
        var error = ResolveThemeColor(application, themeName, Color.FromRgb(0xB9, 0x1C, 0x1C), "Custom.Red", "ValidationError");
        var success = ResolveThemeColor(application, themeName, Color.FromRgb(0x15, 0x80, 0x3D), "Custom.Green", "Accent");
        var warning = ResolveThemeColor(application, themeName, Color.FromRgb(0xB4, 0x53, 0x09), "Custom.Orange", "Custom.Yellow", "Accent");
        var info = ResolveThemeColor(application, themeName, Color.FromRgb(0x1D, 0x4E, 0xD8), "Custom.Blue", "Accent");

        var isDark = IsDark(background);
        var surfaceMuted = ResolveThemeColor(
            application,
            themeName,
            Mix(background, surface, isDark ? 0.56 : 0.68),
            "Button.Background",
            "ControlLight.Background",
            "Control.Background");

        var surfaceAlt = ResolveThemeColor(
            application,
            themeName,
            Mix(surface, border, isDark ? 0.18 : 0.08),
            "HoverBackground",
            "Control.HoverBackground",
            "ControlLight.Background");

        var disabledForeground = ResolveThemeColor(
            application,
            themeName,
            Mix(textPrimary, background, isDark ? 0.56 : 0.62),
            "Foreground.Disabled",
            "DisabledForeground");

        var primaryLight = Lighten(primary, isDark ? 0.18 : 0.10);
        var primaryDark = Darken(primary, isDark ? 0.10 : 0.18);

        return new ThemeAliasPalette(
            Background: background,
            Surface: surface,
            SurfaceMuted: surfaceMuted,
            SurfaceAlt: surfaceAlt,
            Primary: primary,
            PrimaryDark: primaryDark,
            PrimaryLight: primaryLight,
            PrimarySubtle: Mix(surface, primary, isDark ? 0.30 : 0.14),
            TextPrimary: textPrimary,
            TextSecondary: Mix(textPrimary, background, isDark ? 0.26 : 0.42),
            TextMuted: Mix(textPrimary, background, isDark ? 0.42 : 0.58),
            Border: border,
            BorderSoft: Mix(background, border, isDark ? 0.34 : 0.46),
            BorderStrong: Mix(border, textPrimary, isDark ? 0.18 : 0.24),
            Focus: focus,
            Error: error,
            ErrorSubtle: Mix(surface, error, isDark ? 0.24 : 0.12),
            Success: success,
            Warning: warning,
            WarningSubtle: Mix(surface, warning, isDark ? 0.24 : 0.12),
            Info: info,
            InfoSubtle: Mix(surface, info, isDark ? 0.24 : 0.12),
            DisabledBackground: Mix(surfaceMuted, background, isDark ? 0.28 : 0.60),
            DisabledForeground: disabledForeground,
            Selection: selection,
            SelectionBorder: selectionBorder,
            Overlay: Color.FromArgb(isDark ? (byte)0x88 : (byte)0x66, textPrimary.R, textPrimary.G, textPrimary.B),
            CardShadow: Color.FromArgb(isDark ? (byte)0x46 : (byte)0x24, textPrimary.R, textPrimary.G, textPrimary.B),
            WindowGradientTop: Mix(background, surface, isDark ? 0.18 : 0.34),
            WindowGradientMiddle: Mix(background, surface, isDark ? 0.10 : 0.24),
            WindowGradientBottom: Mix(background, textPrimary, isDark ? 0.04 : 0.02),
            BrandingGradientTop: Lighten(primary, isDark ? 0.16 : 0.08),
            BrandingGradientMiddle: primary,
            BrandingGradientBottom: Darken(primary, isDark ? 0.22 : 0.30));
    }

    private static Color ResolveThemeColor(Application application, string themeName, Color fallback, params string[] resourceKeys)
    {
        foreach (var resourceKey in resourceKeys)
        {
            var paletteColorKey = new PaletteColorThemeKeyExtension
            {
                ResourceKey = resourceKey,
                ThemeName = themeName
            };

            if (TryConvertToColor(application.TryFindResource(paletteColorKey), out var paletteColor))
            {
                return paletteColor;
            }

            var paletteBrushKey = new PaletteBrushThemeKeyExtension
            {
                ResourceKey = resourceKey,
                ThemeName = themeName
            };

            if (TryConvertToColor(application.TryFindResource(paletteBrushKey), out paletteColor))
            {
                return paletteColor;
            }
        }

        return fallback;
    }

    private static bool TryConvertToColor(object? resource, out Color color)
    {
        switch (resource)
        {
            case Color resolvedColor:
                color = resolvedColor;
                return true;
            case SolidColorBrush brush:
                color = brush.Color;
                return true;
            default:
                color = default;
                return false;
        }
    }

    private static Color GetExistingColor(ResourceDictionary resources, string key, Color fallback)
    {
        var owner = FindResourceOwner(resources, key);
        if (owner?.Contains(key) == true && owner[key] is Color color)
        {
            return color;
        }

        return fallback;
    }

    private static void SetColorResource(ResourceDictionary resources, string key, Color color)
    {
        var owner = FindResourceOwner(resources, key) ?? resources;
        owner[key] = color;
    }

    private static void SetBrushColor(ResourceDictionary resources, string key, Color color)
    {
        var owner = FindResourceOwner(resources, key) ?? resources;
        if (owner[key] is not SolidColorBrush brush || brush.IsFrozen)
        {
            owner[key] = new SolidColorBrush(color);
            return;
        }

        brush.Color = color;
    }

    private static void SetGradient(ResourceDictionary resources, string key, Color top, Color middle, Color bottom)
    {
        var owner = FindResourceOwner(resources, key) ?? resources;
        if (owner[key] is not LinearGradientBrush gradient || gradient.IsFrozen || gradient.GradientStops.Count < 3)
        {
            owner[key] = CreateGradient(top, middle, bottom);
            return;
        }

        gradient.GradientStops[0].Color = top;
        gradient.GradientStops[1].Color = middle;
        gradient.GradientStops[2].Color = bottom;
    }

    private static ResourceDictionary? FindResourceOwner(ResourceDictionary resources, string key)
    {
        foreach (var candidateKey in resources.Keys)
        {
            if (candidateKey is string stringKey && stringKey == key)
            {
                return resources;
            }
        }

        foreach (var mergedDictionary in resources.MergedDictionaries)
        {
            var owner = FindResourceOwner(mergedDictionary, key);
            if (owner is not null)
            {
                return owner;
            }
        }

        return null;
    }

    private static LinearGradientBrush CreateGradient(Color top, Color middle, Color bottom)
    {
        var gradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1)
        };

        gradient.GradientStops.Add(new GradientStop(top, 0));
        gradient.GradientStops.Add(new GradientStop(middle, 0.5));
        gradient.GradientStops.Add(new GradientStop(bottom, 1));
        return gradient;
    }

    private static bool IsDark(Color color)
    {
        var luminance = ((0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B)) / 255d;
        return luminance < 0.45d;
    }

    private static Color Lighten(Color color, double amount)
    {
        return Mix(color, Colors.White, amount);
    }

    private static Color Darken(Color color, double amount)
    {
        return Mix(color, Colors.Black, amount);
    }

    private static Color Mix(Color from, Color to, double amount)
    {
        var clamped = Math.Clamp(amount, 0d, 1d);
        return Color.FromArgb(
            (byte)Math.Round((from.A * (1d - clamped)) + (to.A * clamped)),
            (byte)Math.Round((from.R * (1d - clamped)) + (to.R * clamped)),
            (byte)Math.Round((from.G * (1d - clamped)) + (to.G * clamped)),
            (byte)Math.Round((from.B * (1d - clamped)) + (to.B * clamped)));
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }

    private sealed record ThemeAliasPalette(
        Color Background,
        Color Surface,
        Color SurfaceMuted,
        Color SurfaceAlt,
        Color Primary,
        Color PrimaryDark,
        Color PrimaryLight,
        Color PrimarySubtle,
        Color TextPrimary,
        Color TextSecondary,
        Color TextMuted,
        Color Border,
        Color BorderSoft,
        Color BorderStrong,
        Color Focus,
        Color Error,
        Color ErrorSubtle,
        Color Success,
        Color Warning,
        Color WarningSubtle,
        Color Info,
        Color InfoSubtle,
        Color DisabledBackground,
        Color DisabledForeground,
        Color Selection,
        Color SelectionBorder,
        Color Overlay,
        Color CardShadow,
        Color WindowGradientTop,
        Color WindowGradientMiddle,
        Color WindowGradientBottom,
        Color BrandingGradientTop,
        Color BrandingGradientMiddle,
        Color BrandingGradientBottom);
}
