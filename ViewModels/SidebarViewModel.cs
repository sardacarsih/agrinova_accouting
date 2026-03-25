namespace Accounting.ViewModels;

public sealed class SidebarViewModel
{
    public SidebarViewModel(MainShellViewModel shell)
    {
        Shell = shell;
    }

    public MainShellViewModel Shell { get; }
}
