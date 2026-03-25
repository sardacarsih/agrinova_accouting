using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Accounting.Services;

public sealed class ThemeService
{
    private static readonly Duration ThemeTransitionDuration = new(TimeSpan.FromMilliseconds(220));

    private static readonly ThemePalette LightPalette = new(
        Background: Color.FromRgb(0xF3, 0xF5, 0xF7),
        Surface: Colors.White,
        SurfaceMuted: Color.FromRgb(0xF8, 0xFA, 0xFB),
        SurfaceAlt: Color.FromRgb(0xF5, 0xF7, 0xFA),
        Primary: Color.FromRgb(0x0F, 0x5F, 0x63),
        PrimaryDark: Color.FromRgb(0x0B, 0x4B, 0x4E),
        PrimaryLight: Color.FromRgb(0x1A, 0x7A, 0x80),
        PrimarySubtle: Color.FromRgb(0xE6, 0xF1, 0xF2),
        TextPrimary: Color.FromRgb(0x1F, 0x29, 0x37),
        TextSecondary: Color.FromRgb(0x6B, 0x72, 0x80),
        TextMuted: Color.FromRgb(0x8A, 0x94, 0xA3),
        Border: Color.FromRgb(0xD7, 0xDE, 0xE5),
        BorderStrong: Color.FromRgb(0xC5, 0xCF, 0xD8),
        Focus: Color.FromRgb(0x1C, 0x7A, 0x93),
        Error: Color.FromRgb(0xB9, 0x1C, 0x1C),
        Success: Color.FromRgb(0x15, 0x80, 0x3D),
        Warning: Color.FromRgb(0xB4, 0x53, 0x09),
        Info: Color.FromRgb(0x1D, 0x4E, 0xD8),
        InfoSubtle: Color.FromRgb(0xE9, 0xEE, 0xFF),
        DisabledBackground: Color.FromRgb(0xF1, 0xF4, 0xF6),
        DisabledForeground: Color.FromRgb(0x9A, 0xA4, 0xB2),
        Selection: Color.FromRgb(0xDD, 0xEB, 0xEF),
        SelectionBorder: Color.FromRgb(0x86, 0xAE, 0xB8),
        WindowGradientTop: Color.FromRgb(0xF6, 0xF8, 0xFA),
        WindowGradientMiddle: Color.FromRgb(0xF3, 0xF5, 0xF7),
        WindowGradientBottom: Color.FromRgb(0xF0, 0xF3, 0xF6),
        BrandingGradientTop: Color.FromRgb(0x0F, 0x5F, 0x63),
        BrandingGradientMiddle: Color.FromRgb(0x0D, 0x55, 0x5A),
        BrandingGradientBottom: Color.FromRgb(0x0B, 0x4B, 0x4E));

    private static readonly ThemePalette DarkPalette = new(
        Background: Color.FromRgb(0x12, 0x1A, 0x25),
        Surface: Color.FromRgb(0x1A, 0x26, 0x33),
        SurfaceMuted: Color.FromRgb(0x22, 0x30, 0x40),
        SurfaceAlt: Color.FromRgb(0x27, 0x36, 0x48),
        Primary: Color.FromRgb(0x3E, 0x9A, 0xA1),
        PrimaryDark: Color.FromRgb(0x2E, 0x7C, 0x82),
        PrimaryLight: Color.FromRgb(0x58, 0xB3, 0xB9),
        PrimarySubtle: Color.FromRgb(0x21, 0x3A, 0x3F),
        TextPrimary: Color.FromRgb(0xE5, 0xE7, 0xEB),
        TextSecondary: Color.FromRgb(0xBF, 0xC8, 0xD6),
        TextMuted: Color.FromRgb(0x93, 0xA4, 0xB8),
        Border: Color.FromRgb(0x3B, 0x4A, 0x5E),
        BorderStrong: Color.FromRgb(0x4D, 0x61, 0x79),
        Focus: Color.FromRgb(0x52, 0xB6, 0xC8),
        Error: Color.FromRgb(0xF8, 0x71, 0x71),
        Success: Color.FromRgb(0x4A, 0xD6, 0x80),
        Warning: Color.FromRgb(0xFB, 0xBF, 0x24),
        Info: Color.FromRgb(0x60, 0xA5, 0xFA),
        InfoSubtle: Color.FromRgb(0x1F, 0x2A, 0x45),
        DisabledBackground: Color.FromRgb(0x27, 0x34, 0x45),
        DisabledForeground: Color.FromRgb(0x78, 0x88, 0x9D),
        Selection: Color.FromRgb(0x27, 0x41, 0x4A),
        SelectionBorder: Color.FromRgb(0x5C, 0xA3, 0xB2),
        WindowGradientTop: Color.FromRgb(0x17, 0x22, 0x2F),
        WindowGradientMiddle: Color.FromRgb(0x13, 0x1D, 0x29),
        WindowGradientBottom: Color.FromRgb(0x0F, 0x17, 0x22),
        BrandingGradientTop: Color.FromRgb(0x15, 0x68, 0x70),
        BrandingGradientMiddle: Color.FromRgb(0x13, 0x5C, 0x63),
        BrandingGradientBottom: Color.FromRgb(0x10, 0x4D, 0x53));

    private static readonly ThemePalette LightHighContrastPalette = new(
        Background: Color.FromRgb(0xEE, 0xF1, 0xF5),
        Surface: Colors.White,
        SurfaceMuted: Color.FromRgb(0xF4, 0xF7, 0xFA),
        SurfaceAlt: Color.FromRgb(0xEC, 0xF1, 0xF6),
        Primary: Color.FromRgb(0x0B, 0x4F, 0x53),
        PrimaryDark: Color.FromRgb(0x08, 0x3B, 0x3F),
        PrimaryLight: Color.FromRgb(0x0F, 0x66, 0x6B),
        PrimarySubtle: Color.FromRgb(0xDC, 0xEA, 0xEC),
        TextPrimary: Color.FromRgb(0x12, 0x1C, 0x28),
        TextSecondary: Color.FromRgb(0x3F, 0x4A, 0x5C),
        TextMuted: Color.FromRgb(0x56, 0x63, 0x78),
        Border: Color.FromRgb(0xB8, 0xC5, 0xD4),
        BorderStrong: Color.FromRgb(0x9E, 0xB0, 0xC3),
        Focus: Color.FromRgb(0x0F, 0x66, 0x8D),
        Error: Color.FromRgb(0x9F, 0x12, 0x12),
        Success: Color.FromRgb(0x16, 0x65, 0x34),
        Warning: Color.FromRgb(0x92, 0x4A, 0x05),
        Info: Color.FromRgb(0x1E, 0x40, 0xAF),
        InfoSubtle: Color.FromRgb(0xE2, 0xE9, 0xFF),
        DisabledBackground: Color.FromRgb(0xE9, 0xEF, 0xF4),
        DisabledForeground: Color.FromRgb(0x6A, 0x79, 0x8F),
        Selection: Color.FromRgb(0xD4, 0xE5, 0xEB),
        SelectionBorder: Color.FromRgb(0x6E, 0x94, 0xA5),
        WindowGradientTop: Color.FromRgb(0xF2, 0xF5, 0xF8),
        WindowGradientMiddle: Color.FromRgb(0xEE, 0xF1, 0xF5),
        WindowGradientBottom: Color.FromRgb(0xE8, 0xEE, 0xF3),
        BrandingGradientTop: Color.FromRgb(0x0B, 0x4F, 0x53),
        BrandingGradientMiddle: Color.FromRgb(0x0A, 0x46, 0x4A),
        BrandingGradientBottom: Color.FromRgb(0x08, 0x3B, 0x3F));

    private static readonly ThemePalette DarkHighContrastPalette = new(
        Background: Color.FromRgb(0x0B, 0x12, 0x1B),
        Surface: Color.FromRgb(0x13, 0x1E, 0x2A),
        SurfaceMuted: Color.FromRgb(0x1B, 0x28, 0x37),
        SurfaceAlt: Color.FromRgb(0x21, 0x30, 0x43),
        Primary: Color.FromRgb(0x74, 0xCB, 0xD2),
        PrimaryDark: Color.FromRgb(0x4F, 0xA8, 0xAF),
        PrimaryLight: Color.FromRgb(0x8E, 0xDE, 0xE4),
        PrimarySubtle: Color.FromRgb(0x25, 0x43, 0x4A),
        TextPrimary: Color.FromRgb(0xF3, 0xF5, 0xF8),
        TextSecondary: Color.FromRgb(0xDB, 0xE3, 0xEE),
        TextMuted: Color.FromRgb(0xBE, 0xCB, 0xDB),
        Border: Color.FromRgb(0x6C, 0x84, 0x9E),
        BorderStrong: Color.FromRgb(0x86, 0xA1, 0xBE),
        Focus: Color.FromRgb(0x89, 0xD8, 0xEA),
        Error: Color.FromRgb(0xFF, 0xA4, 0xA4),
        Success: Color.FromRgb(0x95, 0xF0, 0xB1),
        Warning: Color.FromRgb(0xFF, 0xD1, 0x7A),
        Info: Color.FromRgb(0x9C, 0xC4, 0xFF),
        InfoSubtle: Color.FromRgb(0x2A, 0x39, 0x5B),
        DisabledBackground: Color.FromRgb(0x23, 0x34, 0x49),
        DisabledForeground: Color.FromRgb(0xA1, 0xB0, 0xC4),
        Selection: Color.FromRgb(0x2C, 0x4D, 0x56),
        SelectionBorder: Color.FromRgb(0x87, 0xC3, 0xD1),
        WindowGradientTop: Color.FromRgb(0x11, 0x1B, 0x27),
        WindowGradientMiddle: Color.FromRgb(0x0D, 0x15, 0x20),
        WindowGradientBottom: Color.FromRgb(0x09, 0x10, 0x1A),
        BrandingGradientTop: Color.FromRgb(0x1B, 0x7A, 0x83),
        BrandingGradientMiddle: Color.FromRgb(0x17, 0x6A, 0x72),
        BrandingGradientBottom: Color.FromRgb(0x13, 0x5A, 0x61));

    public void ApplyTheme(ThemeMode mode, bool highContrast, bool animate = true)
    {
        var resources = Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        var resolvedMode = mode == ThemeMode.System ? DetectSystemTheme() : mode;
        var palette = (resolvedMode, highContrast) switch
        {
            (ThemeMode.Dark, true) => DarkHighContrastPalette,
            (ThemeMode.Dark, false) => DarkPalette,
            (ThemeMode.Light, true) => LightHighContrastPalette,
            _ => LightPalette
        };

        SetBrushColor(resources, "Brush.Background", palette.Background, animate);
        SetBrushColor(resources, "Brush.Surface", palette.Surface, animate);
        SetBrushColor(resources, "Brush.SurfaceMuted", palette.SurfaceMuted, animate);
        SetBrushColor(resources, "Brush.SurfaceAlt", palette.SurfaceAlt, animate);

        SetBrushColor(resources, "Brush.Primary", palette.Primary, animate);
        SetBrushColor(resources, "Brush.PrimaryDark", palette.PrimaryDark, animate);
        SetBrushColor(resources, "Brush.PrimaryLight", palette.PrimaryLight, animate);
        SetBrushColor(resources, "Brush.PrimarySubtle", palette.PrimarySubtle, animate);

        SetBrushColor(resources, "Brush.TextPrimary", palette.TextPrimary, animate);
        SetBrushColor(resources, "Brush.TextSecondary", palette.TextSecondary, animate);
        SetBrushColor(resources, "Brush.TextMuted", palette.TextMuted, animate);

        SetBrushColor(resources, "Brush.Border", palette.Border, animate);
        SetBrushColor(resources, "Brush.BorderStrong", palette.BorderStrong, animate);
        SetBrushColor(resources, "Brush.Focus", palette.Focus, animate);

        SetBrushColor(resources, "Brush.Error", palette.Error, animate);
        SetBrushColor(resources, "Brush.Success", palette.Success, animate);
        SetBrushColor(resources, "Brush.Warning", palette.Warning, animate);
        SetBrushColor(resources, "Brush.Info", palette.Info, animate);
        SetBrushColor(resources, "Brush.InfoSubtle", palette.InfoSubtle, animate);

        SetBrushColor(resources, "Brush.DisabledBackground", palette.DisabledBackground, animate);
        SetBrushColor(resources, "Brush.DisabledForeground", palette.DisabledForeground, animate);
        SetBrushColor(resources, "Brush.Selection", palette.Selection, animate);
        SetBrushColor(resources, "Brush.SelectionBorder", palette.SelectionBorder, animate);

        SetGradient(
            resources,
            "Brush.WindowGradient",
            palette.WindowGradientTop,
            palette.WindowGradientMiddle,
            palette.WindowGradientBottom,
            animate);

        SetGradient(
            resources,
            "Brush.BrandingGradient",
            palette.BrandingGradientTop,
            palette.BrandingGradientMiddle,
            palette.BrandingGradientBottom,
            animate);
    }

    private static ThemeMode DetectSystemTheme()
    {
        try
        {
            var personalize = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = personalize?.GetValue("AppsUseLightTheme");
            if (value is int intValue)
            {
                return intValue == 0 ? ThemeMode.Dark : ThemeMode.Light;
            }
        }
        catch (Exception)
        {
            // fallback below
        }

        return ThemeMode.Light;
    }

    private static void SetBrushColor(ResourceDictionary resources, string key, Color color, bool animate)
    {
        if (!resources.Contains(key))
        {
            return;
        }

        if (resources[key] is not SolidColorBrush brush)
        {
            resources[key] = new SolidColorBrush(color);
            return;
        }

        if (brush.IsFrozen)
        {
            resources[key] = new SolidColorBrush(color);
            return;
        }

        try
        {
            if (!animate)
            {
                brush.Color = color;
                return;
            }

            AnimateBrushTo(brush, color);
        }
        catch (InvalidOperationException)
        {
            resources[key] = new SolidColorBrush(color);
        }
    }

    private static void SetGradient(ResourceDictionary resources, string key, Color top, Color middle, Color bottom, bool animate)
    {
        if (!resources.Contains(key))
        {
            return;
        }

        if (resources[key] is not LinearGradientBrush gradient || gradient.GradientStops.Count < 3)
        {
            resources[key] = CreateGradient(top, middle, bottom);
            return;
        }

        if (gradient.IsFrozen)
        {
            resources[key] = CreateGradient(top, middle, bottom);
            return;
        }

        try
        {
            if (!animate)
            {
                gradient.GradientStops[0].Color = top;
                gradient.GradientStops[1].Color = middle;
                gradient.GradientStops[2].Color = bottom;
                return;
            }

            AnimateGradientStopTo(gradient.GradientStops[0], top);
            AnimateGradientStopTo(gradient.GradientStops[1], middle);
            AnimateGradientStopTo(gradient.GradientStops[2], bottom);
        }
        catch (InvalidOperationException)
        {
            resources[key] = CreateGradient(top, middle, bottom);
        }
    }

    private static void AnimateBrushTo(SolidColorBrush brush, Color targetColor)
    {
        var animation = new ColorAnimation
        {
            To = targetColor,
            Duration = ThemeTransitionDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        brush.BeginAnimation(SolidColorBrush.ColorProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static void AnimateGradientStopTo(GradientStop stop, Color targetColor)
    {
        var animation = new ColorAnimation
        {
            To = targetColor,
            Duration = ThemeTransitionDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        stop.BeginAnimation(GradientStop.ColorProperty, animation, HandoffBehavior.SnapshotAndReplace);
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

    private sealed record ThemePalette(
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
        Color BorderStrong,
        Color Focus,
        Color Error,
        Color Success,
        Color Warning,
        Color Info,
        Color InfoSubtle,
        Color DisabledBackground,
        Color DisabledForeground,
        Color Selection,
        Color SelectionBorder,
        Color WindowGradientTop,
        Color WindowGradientMiddle,
        Color WindowGradientBottom,
        Color BrandingGradientTop,
        Color BrandingGradientMiddle,
        Color BrandingGradientBottom);
}

