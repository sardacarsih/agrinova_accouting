using System.Windows.Controls;
using Accounting.ViewModels;

namespace Accounting.Views.Components;

public partial class UserManagementWorkspaceControl : UserControl
{
    private bool _isRevertingSelection;
    private int _previousSelectedIndex = 0;

    public UserManagementWorkspaceControl()
    {
        InitializeComponent();
    }

    private void UserManagementTabControl_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRevertingSelection || sender is not TabControl tabControl)
        {
            return;
        }

        var newIndex = tabControl.SelectedIndex;
        if (_previousSelectedIndex == 1 &&
            newIndex != 1 &&
            DataContext is UserManagementViewModel viewModel &&
            !viewModel.TryLeaveRoleEditor())
        {
            _isRevertingSelection = true;
            tabControl.SelectedIndex = 1;
            _isRevertingSelection = false;
            return;
        }

        _previousSelectedIndex = newIndex;
    }
}
