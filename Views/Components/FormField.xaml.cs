using System.Windows;
using System.Windows.Controls;

namespace Accounting.Views.Components;

public partial class FormField : UserControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(FormField), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
        nameof(Hint), typeof(string), typeof(FormField), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty FieldContentProperty = DependencyProperty.Register(
        nameof(FieldContent), typeof(object), typeof(FormField), new PropertyMetadata(null));

    public FormField()
    {
        InitializeComponent();
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string Hint
    {
        get => (string)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    public object? FieldContent
    {
        get => GetValue(FieldContentProperty);
        set => SetValue(FieldContentProperty, value);
    }
}
