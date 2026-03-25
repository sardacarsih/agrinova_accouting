using System.Collections;
using System.Windows.Input;
using Accounting.Infrastructure;

namespace Accounting.ViewModels;

public sealed class GridViewModel : ViewModelBase
{
    private IEnumerable? _items;
    private object? _selectedItem;

    public IEnumerable? Items
    {
        get => _items;
        set => SetProperty(ref _items, value);
    }

    public object? SelectedItem
    {
        get => _selectedItem;
        set => SetProperty(ref _selectedItem, value);
    }

    public ICommand? RefreshCommand { get; set; }

    public ICommand? OpenCommand { get; set; }
}
