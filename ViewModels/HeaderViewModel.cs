namespace Accounting.ViewModels;

public sealed class HeaderViewModel
{
    public HeaderViewModel(MainShellViewModel shell)
    {
        Shell = shell;
    }

    public MainShellViewModel Shell { get; }
}
