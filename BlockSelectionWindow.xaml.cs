using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using DevExpress.Xpf.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Accounting.Services;

namespace Accounting;

public partial class BlockSelectionWindow : ThemedWindow, INotifyPropertyChanged
{
    private readonly List<ManagedCostCenter> _allBlocks;
    private string _resultCountText = string.Empty;

    public BlockSelectionWindow(IEnumerable<ManagedCostCenter> blocks, string? initialFilter = null)
    {
        InitializeComponent();
        DataContext = this;

        _allBlocks = (blocks ?? Array.Empty<ManagedCostCenter>())
            .Where(x => x.IsActive)
            .OrderBy(x => x.CostCenterCode)
            .ToList();

        var normalizedInitialFilter = initialFilter?.Trim() ?? string.Empty;
        SearchTextBox.Text = normalizedInitialFilter;
        ApplyFilter(normalizedInitialFilter);

        Loaded += (_, _) =>
        {
            SearchTextBox.Focus();
            SearchTextBox.CaretIndex = SearchTextBox.Text.Length;
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ManagedCostCenter> FilteredBlocks { get; } = new();

    public ManagedCostCenter? SelectedBlock => BlocksGrid.SelectedItem as ManagedCostCenter;

    public string ResultCountText
    {
        get => _resultCountText;
        private set
        {
            if (string.Equals(_resultCountText, value, StringComparison.Ordinal))
            {
                return;
            }

            _resultCountText = value;
            OnPropertyChanged();
        }
    }

    private void SearchTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter(SearchTextBox.Text);
    }

    private void BlocksGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectButton.IsEnabled = SelectedBlock is not null;
    }

    private void BlocksGrid_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ConfirmSelection();
    }

    private void SelectButton_OnClick(object sender, RoutedEventArgs e)
    {
        ConfirmSelection();
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DialogResult = false;
            return;
        }

        if (e.Key != Key.Enter)
        {
            return;
        }

        if (ConfirmSelection())
        {
            e.Handled = true;
        }
    }

    private void ApplyFilter(string? rawFilter)
    {
        var filter = rawFilter?.Trim() ?? string.Empty;

        IEnumerable<ManagedCostCenter> query = _allBlocks;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            query = query.Where(x =>
                x.CostCenterCode.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                x.EstateCode.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                x.DivisionCode.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                x.BlockCode.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                x.BlockName.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        var snapshot = query.ToList();

        FilteredBlocks.Clear();
        foreach (var block in snapshot)
        {
            FilteredBlocks.Add(block);
        }

        BlocksGrid.SelectedItem = FilteredBlocks.FirstOrDefault();
        SelectButton.IsEnabled = SelectedBlock is not null;
        ResultCountText = $"{FilteredBlocks.Count} blok ditampilkan.";
    }

    private bool ConfirmSelection()
    {
        if (SelectedBlock is null)
        {
            return false;
        }

        DialogResult = true;
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
