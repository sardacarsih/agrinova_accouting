using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using DevExpress.Xpf.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Accounting.Services;

namespace Accounting;

public partial class AccountSelectionWindow : ThemedWindow, INotifyPropertyChanged
{
    private readonly List<ManagedAccount> _allAccounts;
    private string _resultCountText = string.Empty;

    public AccountSelectionWindow(IEnumerable<ManagedAccount> accounts, string? initialFilter = null)
    {
        InitializeComponent();
        DataContext = this;

        _allAccounts = (accounts ?? Array.Empty<ManagedAccount>())
            .Where(x => x.IsActive)
            .OrderBy(x => x.Code)
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

    public ObservableCollection<ManagedAccount> FilteredAccounts { get; } = new();

    public ManagedAccount? SelectedAccount => AccountsGrid.SelectedItem as ManagedAccount;

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

    private void AccountsGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectButton.IsEnabled = SelectedAccount is not null;
    }

    private void AccountsGrid_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
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

        IEnumerable<ManagedAccount> query = _allAccounts;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            query = query.Where(x =>
                x.Code.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                x.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        var snapshot = query.ToList();

        FilteredAccounts.Clear();
        foreach (var account in snapshot)
        {
            FilteredAccounts.Add(account);
        }

        if (FilteredAccounts.Count > 0)
        {
            AccountsGrid.SelectedItem = FilteredAccounts[0];
        }
        else
        {
            AccountsGrid.SelectedItem = null;
        }

        SelectButton.IsEnabled = SelectedAccount is not null;
        ResultCountText = $"{FilteredAccounts.Count} akun ditampilkan.";
    }

    private bool ConfirmSelection()
    {
        if (SelectedAccount is null)
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

