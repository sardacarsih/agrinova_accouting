namespace Accounting.ViewModels;

public sealed class WorkspaceViewModel
{
    public WorkspaceViewModel(MainShellViewModel shell)
    {
        Shell = shell;
    }

    public MainShellViewModel Shell { get; }
}
