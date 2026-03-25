using System.Windows;
using System.Windows.Controls;

namespace Accounting.Views.Components;

public partial class FormSectionControl : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(FormSectionControl), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SectionContentProperty = DependencyProperty.Register(
        nameof(SectionContent), typeof(object), typeof(FormSectionControl), new PropertyMetadata(null));

    public FormSectionControl()
    {
        InitializeComponent();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public object? SectionContent
    {
        get => GetValue(SectionContentProperty);
        set => SetValue(SectionContentProperty, value);
    }
}
