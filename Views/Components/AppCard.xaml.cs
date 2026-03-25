using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Accounting.Views.Components;

public partial class AppCard : UserControl
{
    public static readonly DependencyProperty CardContentProperty = DependencyProperty.Register(
        nameof(CardContent), typeof(object), typeof(AppCard), new PropertyMetadata(null));

    public static readonly DependencyProperty CardPaddingProperty = DependencyProperty.Register(
        nameof(CardPadding), typeof(Thickness), typeof(AppCard), new PropertyMetadata(new Thickness(12)));

    public static readonly DependencyProperty CardCornerRadiusProperty = DependencyProperty.Register(
        nameof(CardCornerRadius), typeof(CornerRadius), typeof(AppCard), new PropertyMetadata(new CornerRadius(10)));

    public static readonly DependencyProperty CardBackgroundProperty = DependencyProperty.Register(
        nameof(CardBackground), typeof(Brush), typeof(AppCard), new PropertyMetadata(null));

    public static readonly DependencyProperty CardBorderBrushProperty = DependencyProperty.Register(
        nameof(CardBorderBrush), typeof(Brush), typeof(AppCard), new PropertyMetadata(null));

    public static readonly DependencyProperty CardBorderThicknessProperty = DependencyProperty.Register(
        nameof(CardBorderThickness), typeof(Thickness), typeof(AppCard), new PropertyMetadata(new Thickness(1)));

    public AppCard()
    {
        InitializeComponent();
    }

    public object? CardContent
    {
        get => GetValue(CardContentProperty);
        set => SetValue(CardContentProperty, value);
    }

    public Thickness CardPadding
    {
        get => (Thickness)GetValue(CardPaddingProperty);
        set => SetValue(CardPaddingProperty, value);
    }

    public CornerRadius CardCornerRadius
    {
        get => (CornerRadius)GetValue(CardCornerRadiusProperty);
        set => SetValue(CardCornerRadiusProperty, value);
    }

    public Brush? CardBackground
    {
        get => (Brush?)GetValue(CardBackgroundProperty);
        set => SetValue(CardBackgroundProperty, value);
    }

    public Brush? CardBorderBrush
    {
        get => (Brush?)GetValue(CardBorderBrushProperty);
        set => SetValue(CardBorderBrushProperty, value);
    }

    public Thickness CardBorderThickness
    {
        get => (Thickness)GetValue(CardBorderThicknessProperty);
        set => SetValue(CardBorderThicknessProperty, value);
    }
}
