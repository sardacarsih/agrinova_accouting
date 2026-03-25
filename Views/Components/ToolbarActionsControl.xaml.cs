using System.Windows;
using System.Windows.Controls;

namespace Accounting.Views.Components;

public partial class ToolbarActionsControl : UserControl
{
    public static readonly DependencyProperty ActionsContentProperty = DependencyProperty.Register(
        nameof(ActionsContent), typeof(object), typeof(ToolbarActionsControl), new PropertyMetadata(null));

    public ToolbarActionsControl()
    {
        InitializeComponent();
    }

    public object? ActionsContent
    {
        get => GetValue(ActionsContentProperty);
        set => SetValue(ActionsContentProperty, value);
    }
}
