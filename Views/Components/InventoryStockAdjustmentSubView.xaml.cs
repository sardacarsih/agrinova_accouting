using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Accounting.ViewModels;

namespace Accounting.Views.Components;

public partial class InventoryStockAdjustmentSubView : UserControl
{
    public InventoryStockAdjustmentSubView()
    {
        InitializeComponent();
    }

    private void LookupTextBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
            if (!HandleLookupCommit(textBox))
            {
                FocusFirstSuggestion(textBox);
            }

            return;
        }

        if (e.Key == Key.Down && IsLookupPopupOpen(textBox))
        {
            e.Handled = true;
            FocusFirstSuggestion(textBox);
            return;
        }

        if (e.Key == Key.Escape)
        {
            CloseLookupPopup(textBox);
            e.Handled = true;
        }
    }

    private void LookupTextBox_OnPreviewLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        if (IsFocusMovingWithinLookupPopup(textBox.DataContext, e.NewFocus as DependencyObject))
        {
            return;
        }

        textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        if (!HandleLookupCommit(textBox))
        {
            e.Handled = true;
            FocusFirstSuggestion(textBox);
        }
    }

    private void LookupListBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ListBox listBox)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            TryApplySelectedLookup(listBox);
            return;
        }

        if (e.Key == Key.Escape)
        {
            CloseLookupPopup(listBox);
            e.Handled = true;
        }
    }

    private void LookupListBox_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox listBox)
        {
            TryApplySelectedLookup(listBox);
        }
    }

    private void LookupListBox_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox listBox)
        {
            TryApplySelectedLookup(listBox);
        }
    }

    private bool HandleLookupCommit(FrameworkElement source)
    {
        if (DataContext is not InventoryViewModel viewModel ||
            source.DataContext is not StockTransactionLineEditor line)
        {
            return true;
        }

        return (source.Tag as string) switch
        {
            "ItemLookup" => viewModel.CommitItemLookup(line),
            _ => true
        };
    }

    private bool TryApplySelectedLookup(FrameworkElement source)
    {
        if (DataContext is not InventoryViewModel viewModel ||
            source.DataContext is not StockTransactionLineEditor line)
        {
            return false;
        }

        var applied = (source.Tag as string) switch
        {
            "ItemLookup" => viewModel.ApplySelectedItemLookupOption(line, line.SelectedItemLookupSuggestion),
            _ => false
        };

        if (applied)
        {
            CloseLookupPopup(source);
        }

        return applied;
    }

    private void FocusFirstSuggestion(TextBox textBox)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var listBox = FindSiblingLookupListBox(textBox);
            if (listBox is null || listBox.Items.Count == 0)
            {
                return;
            }

            listBox.UpdateLayout();
            listBox.SelectedIndex = Math.Max(0, listBox.SelectedIndex);
            listBox.Focus();

            if (listBox.ItemContainerGenerator.ContainerFromIndex(listBox.SelectedIndex) is ListBoxItem item)
            {
                item.Focus();
            }
        }, DispatcherPriority.Input);
    }

    private static bool IsLookupPopupOpen(FrameworkElement source)
    {
        return source.DataContext is StockTransactionLineEditor line &&
               string.Equals(source.Tag as string, "ItemLookup", StringComparison.Ordinal) &&
               line.IsItemLookupPopupOpen;
    }

    private static void CloseLookupPopup(FrameworkElement source)
    {
        if (source.DataContext is StockTransactionLineEditor line &&
            string.Equals(source.Tag as string, "ItemLookup", StringComparison.Ordinal))
        {
            line.IsItemLookupPopupOpen = false;
        }
    }

    private static bool IsFocusMovingWithinLookupPopup(object? lineContext, DependencyObject? newFocus)
    {
        if (lineContext is null || newFocus is null)
        {
            return false;
        }

        var listBox = FindAncestor<ListBox>(newFocus);
        return listBox?.DataContext == lineContext;
    }

    private static ListBox? FindSiblingLookupListBox(DependencyObject source)
    {
        var container = FindAncestor<Grid>(source);
        if (container is null)
        {
            return null;
        }

        foreach (var child in container.Children)
        {
            if (child is not Popup popup || popup.Child is null)
            {
                continue;
            }

            var listBox = FindVisualChild<ListBox>(popup.Child);
            if (listBox is not null)
            {
                return listBox;
            }
        }

        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
            {
                return match;
            }

            source = source switch
            {
                FrameworkContentElement contentElement => contentElement.Parent,
                _ => VisualTreeHelper.GetParent(source)
            };
        }

        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent is null)
        {
            return null;
        }

        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            var nested = FindVisualChild<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }
}
