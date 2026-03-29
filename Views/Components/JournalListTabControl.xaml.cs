using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DevExpress.Xpf.Grid;
using Accounting.ViewModels;

namespace Accounting.Views.Components;

public partial class JournalListTabControl : UserControl
{
    private JournalManagementViewModel? _subscribedViewModel;
    private bool _isSynchronizingMasterSelection;

    public JournalListTabControl()
    {
        InitializeComponent();
        DataContextChanged += JournalListTabControl_OnDataContextChanged;
        Loaded += JournalListTabControl_OnLoaded;
        Unloaded += JournalListTabControl_OnUnloaded;
    }

    private async void JournalListGrid_OnSelectionChanged(object sender, GridSelectionChangedEventArgs e)
    {
        if (_isSynchronizingMasterSelection || DataContext is not JournalManagementViewModel viewModel)
        {
            return;
        }

        viewModel.SetSelectedBrowseJournalRows(JournalListGrid.SelectedItems.OfType<JournalBrowseRowViewModel>());
        await viewModel.EnsureBrowseJournalDetailLoadedAsync(viewModel.ActiveBrowseJournalRow);
    }

    private void JournalListGrid_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not JournalManagementViewModel viewModel)
        {
            return;
        }

        if (!ReferenceEquals(FindAncestor<GridControl>(e.OriginalSource as DependencyObject), JournalListGrid))
        {
            return;
        }

        if (JournalListGrid.SelectedItem is not JournalBrowseRowViewModel row)
        {
            return;
        }

        viewModel.SetSelectedBrowseJournalRows(new[] { row });
        if (viewModel.OpenSelectedJournalCommand.CanExecute(null))
        {
            viewModel.OpenSelectedJournalCommand.Execute(null);
        }
    }

    private void JournalListTabControl_OnLoaded(object sender, RoutedEventArgs e)
    {
        SyncMasterGridSelectionFromViewModel();
    }

    private void JournalListTabControl_OnUnloaded(object sender, RoutedEventArgs e)
    {
        UnsubscribeFromViewModel();
    }

    private void JournalListTabControl_OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UnsubscribeFromViewModel();
        SubscribeToViewModel(e.NewValue as JournalManagementViewModel);
        SyncMasterGridSelectionFromViewModel();
    }

    private void SubscribeToViewModel(JournalManagementViewModel? viewModel)
    {
        if (viewModel is null)
        {
            return;
        }

        _subscribedViewModel = viewModel;
        _subscribedViewModel.PropertyChanged += ViewModel_OnPropertyChanged;
    }

    private void UnsubscribeFromViewModel()
    {
        if (_subscribedViewModel is null)
        {
            return;
        }

        _subscribedViewModel.PropertyChanged -= ViewModel_OnPropertyChanged;
        _subscribedViewModel = null;
    }

    private void ViewModel_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.PropertyName) ||
            e.PropertyName == nameof(JournalManagementViewModel.ActiveBrowseJournalRow))
        {
            Dispatcher.Invoke(SyncMasterGridSelectionFromViewModel);
        }
    }

    private void SyncMasterGridSelectionFromViewModel()
    {
        if (DataContext is not JournalManagementViewModel viewModel)
        {
            return;
        }

        _isSynchronizingMasterSelection = true;
        try
        {
            var activeRow = viewModel.ActiveBrowseJournalRow;
            JournalListGrid.SelectedItems.Clear();
            JournalListGrid.SelectedItem = null;

            if (activeRow is not null)
            {
                JournalListGrid.SelectedItems.Add(activeRow);
                JournalListGrid.SelectedItem = activeRow;
                _ = viewModel.EnsureBrowseJournalDetailLoadedAsync(activeRow);
            }
        }
        finally
        {
            _isSynchronizingMasterSelection = false;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current)
        where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
