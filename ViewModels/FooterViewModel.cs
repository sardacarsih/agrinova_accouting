namespace Accounting.ViewModels;

public sealed class FooterViewModel
{
    public FooterViewModel(MainShellViewModel shell)
    {
        Shell = shell;
    }

    public MainShellViewModel Shell { get; }
}
