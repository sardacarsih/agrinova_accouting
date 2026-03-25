using System.Windows.Input;
using Accounting.Infrastructure;

namespace Accounting.ViewModels;

public sealed class FormViewModel : ViewModelBase
{
    private string _title = string.Empty;
    private string _statusText = string.Empty;

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public ICommand? PrimaryCommand { get; set; }

    public ICommand? SecondaryCommand { get; set; }
}
