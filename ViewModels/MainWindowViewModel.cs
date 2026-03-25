using System.Windows;
using Accounting.Services;
using Accounting.Infrastructure;

namespace Accounting.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase, IDisposable
{
    public MainWindowViewModel(MainShellViewModel shell)
    {
        Shell = shell;
        Header = new HeaderViewModel(shell);
        Sidebar = new SidebarViewModel(shell);
        Workspace = new WorkspaceViewModel(shell);
        Footer = new FooterViewModel(shell);

        SharedForm = new FormViewModel();
        SharedGrid = new GridViewModel();
    }

    public MainShellViewModel Shell { get; }

    public HeaderViewModel Header { get; }

    public SidebarViewModel Sidebar { get; }

    public WorkspaceViewModel Workspace { get; }

    public FooterViewModel Footer { get; }

    public FormViewModel SharedForm { get; }

    public GridViewModel SharedGrid { get; }

    public void UpdateLayout(double width, double dpiScale)
    {
        Shell.UpdateLayout(width, dpiScale);
    }

    public void Dispose()
    {
        Shell.Dispose();
    }
}
