using System.Windows;
using System.Windows.Controls;

namespace Accounting.Views.Components;

public partial class DataGridSectionControl : UserControl
{
    public static readonly DependencyProperty ToolbarContentProperty = DependencyProperty.Register(
        nameof(ToolbarContent), typeof(object), typeof(DataGridSectionControl), new PropertyMetadata(null));

    public static readonly DependencyProperty GridContentProperty = DependencyProperty.Register(
        nameof(GridContent), typeof(object), typeof(DataGridSectionControl), new PropertyMetadata(null));

    public DataGridSectionControl()
    {
        InitializeComponent();
    }

    public object? ToolbarContent
    {
        get => GetValue(ToolbarContentProperty);
        set => SetValue(ToolbarContentProperty, value);
    }

    public object? GridContent
    {
        get => GetValue(GridContentProperty);
        set => SetValue(GridContentProperty, value);
    }
}
