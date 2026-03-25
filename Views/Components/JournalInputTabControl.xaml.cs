using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Accounting.ViewModels;

namespace Accounting.Views.Components;

public partial class JournalInputTabControl : UserControl
{
    private const string JournalLineDragFormat = "Accounting.JournalInputRows";
    private Point? _dragStartPoint;

    public JournalInputTabControl()
    {
        InitializeComponent();
    }

    private void JournalLinesGrid_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(JournalLinesGrid);
    }

    private void JournalLinesGrid_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStartPoint is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        if (Math.Abs(e.GetPosition(JournalLinesGrid).X - _dragStartPoint.Value.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(e.GetPosition(JournalLinesGrid).Y - _dragStartPoint.Value.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var origin = e.OriginalSource as DependencyObject;
        if (FindAncestor<TextBox>(origin) is not null)
        {
            return;
        }

        var selectedRows = JournalLinesGrid.SelectedItems
            .OfType<JournalLineEditor>()
            .ToList();

        if (selectedRows.Count == 0 &&
            TryGetLineFromEventSource(origin, out var line, out _))
        {
            selectedRows.Add(line);
            JournalLinesGrid.SelectedItems.Clear();
            JournalLinesGrid.SelectedItems.Add(line);
        }

        if (selectedRows.Count == 0)
        {
            return;
        }

        _dragStartPoint = null;
        var data = new DataObject(JournalLineDragFormat, selectedRows);
        DragDrop.DoDragDrop(JournalLinesGrid, data, DragDropEffects.Move);
    }

    private void JournalLinesGrid_OnDragOver(object sender, DragEventArgs e)
    {
        if (DataContext is not JournalManagementViewModel)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (e.Data.GetData(JournalLineDragFormat) is not List<JournalLineEditor> movingLines || movingLines.Count == 0)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var dropPosition = e.GetPosition(JournalLinesGrid);
        if (!TryGetDropTarget(e.OriginalSource as DependencyObject, dropPosition, out _, out _))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void JournalLinesGrid_OnDrop(object sender, DragEventArgs e)
    {
        _dragStartPoint = null;

        if (DataContext is not JournalManagementViewModel viewModel)
        {
            return;
        }

        if (e.Data.GetData(JournalLineDragFormat) is not List<JournalLineEditor> movingLines || movingLines.Count == 0)
        {
            return;
        }

        var dropPosition = e.GetPosition(JournalLinesGrid);
        if (!TryGetDropTarget(e.OriginalSource as DependencyObject, dropPosition, out var targetIndex, out var insertBefore))
        {
            return;
        }

        var moved = viewModel.MoveInputLines(movingLines, targetIndex, insertBefore);
        if (moved.Count == 0)
        {
            return;
        }

        JournalLinesGrid.SelectedItems.Clear();
        foreach (var movedLine in moved)
        {
            JournalLinesGrid.SelectedItems.Add(movedLine);
        }

        JournalLinesGrid.ScrollIntoView(moved[0]);
        e.Handled = true;
    }

    private bool TryGetDropTarget(DependencyObject? source, Point gridPosition, out int targetIndex, out bool insertBefore)
    {
        targetIndex = -1;
        insertBefore = true;

        if (TryGetLineFromEventSource(source, out _, out var rowIndex))
        {
            targetIndex = rowIndex;
            var row = FindAncestor<DataGridRow>(source);
            if (row is not null)
            {
                var rowTop = row.TranslatePoint(new Point(0, 0), JournalLinesGrid).Y;
                var rowMidpoint = rowTop + (row.ActualHeight / 2d);
                insertBefore = gridPosition.Y <= rowMidpoint;
            }

            return true;
        }

        if (JournalLinesGrid.Items.Count <= 0)
        {
            return false;
        }

        targetIndex = JournalLinesGrid.Items.Count - 1;
        insertBefore = false;
        return true;
    }

    private void UserControl_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F6)
        {
            var reverse = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            MoveFocusToNextZone(reverse);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && HandleEscapeInGridEditor())
        {
            e.Handled = true;
        }
    }

    private void JournalLinesGrid_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        if (DataContext is not JournalManagementViewModel viewModel)
        {
            return;
        }

        if (Keyboard.FocusedElement is TextBox activeEditor)
        {
            activeEditor.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        }

        var context = GetCurrentGridContext();
        if (context is null)
        {
            return;
        }

        var (currentLine, currentColumn) = context.Value;
        var editableColumns = GetEditableColumns();
        var currentColumnIndex = editableColumns.IndexOf(currentColumn);
        if (currentColumnIndex < 0)
        {
            return;
        }

        e.Handled = true;
        JournalLinesGrid.CommitEdit(DataGridEditingUnit.Cell, true);

        if (ReferenceEquals(currentColumn, AccountCodeColumn))
        {
            if (!IsAccountLineValid(currentLine))
            {
                TryRunAccountPicker(viewModel, currentLine);
            }

            if (!IsAccountLineValid(currentLine))
            {
                MoveFocusToGridCell(currentLine, AccountCodeColumn);
                return;
            }
        }

        if (currentColumnIndex < editableColumns.Count - 1)
        {
            MoveFocusToGridCell(currentLine, editableColumns[currentColumnIndex + 1]);
            return;
        }

        var rowIndex = viewModel.InputLines.IndexOf(currentLine);
        if (rowIndex >= 0 && rowIndex < viewModel.InputLines.Count - 1)
        {
            MoveFocusToGridCell(viewModel.InputLines[rowIndex + 1], editableColumns[0]);
            return;
        }

        var appendedLine = viewModel.AppendLineFrom(currentLine);
        MoveFocusToGridCell(appendedLine, editableColumns[0]);
    }

    private static bool IsAccountLineValid(JournalLineEditor line)
    {
        return !line.HasValidationError &&
               !string.IsNullOrWhiteSpace(line.AccountCode) &&
               !string.IsNullOrWhiteSpace(line.AccountName);
    }

    private static void TryRunAccountPicker(JournalManagementViewModel viewModel, JournalLineEditor line)
    {
        var command = viewModel.OpenAccountPickerCommand;
        if (command.CanExecute(line))
        {
            command.Execute(line);
        }
    }

    private void MoveFocusToGridCell(object item, DataGridColumn? column)
    {
        if (column is null)
        {
            return;
        }

        JournalLinesGrid.SelectedItem = item;
        JournalLinesGrid.CurrentCell = new DataGridCellInfo(item, column);
        JournalLinesGrid.ScrollIntoView(item, column);
        JournalLinesGrid.Focus();
        JournalLinesGrid.BeginEdit();

        Dispatcher.BeginInvoke(() =>
        {
            if (TryFocusGridCellContent(item, column))
            {
                return;
            }

            JournalLinesGrid.Focus();
        }, DispatcherPriority.Input);
    }

    private bool TryFocusGridCellContent(object item, DataGridColumn column)
    {
        var cell = GetGridCell(item, column);
        if (cell is null)
        {
            return false;
        }

        if (FindVisualChild<TextBox>(cell) is TextBox textBox)
        {
            textBox.Focus();
            textBox.SelectAll();
            return true;
        }

        if (cell.Content is FrameworkElement content && content.Focus())
        {
            return true;
        }

        return cell.Focus();
    }

    private DataGridCell? GetGridCell(object item, DataGridColumn column)
    {
        var row = JournalLinesGrid.ItemContainerGenerator.ContainerFromItem(item) as DataGridRow;
        if (row is null)
        {
            JournalLinesGrid.UpdateLayout();
            JournalLinesGrid.ScrollIntoView(item, column);
            row = JournalLinesGrid.ItemContainerGenerator.ContainerFromItem(item) as DataGridRow;
            if (row is null)
            {
                return null;
            }
        }

        var presenter = FindVisualChild<DataGridCellsPresenter>(row);
        if (presenter is null)
        {
            row.ApplyTemplate();
            presenter = FindVisualChild<DataGridCellsPresenter>(row);
            if (presenter is null)
            {
                return null;
            }
        }

        var cell = presenter.ItemContainerGenerator.ContainerFromIndex(column.DisplayIndex) as DataGridCell;
        if (cell is not null)
        {
            return cell;
        }

        JournalLinesGrid.ScrollIntoView(row.Item, column);
        return presenter.ItemContainerGenerator.ContainerFromIndex(column.DisplayIndex) as DataGridCell;
    }

    private (JournalLineEditor Line, DataGridColumn Column)? GetCurrentGridContext()
    {
        var focused = Keyboard.FocusedElement as DependencyObject;
        var currentCell = FindAncestor<DataGridCell>(focused);
        if (currentCell?.DataContext is not JournalLineEditor line || currentCell.Column is null)
        {
            return null;
        }

        return (line, currentCell.Column);
    }

    private bool TryGetLineFromEventSource(DependencyObject? source, out JournalLineEditor line, out int index)
    {
        line = null!;
        index = -1;

        var row = FindAncestor<DataGridRow>(source);
        if (row?.DataContext is not JournalLineEditor rowLine)
        {
            return false;
        }

        line = rowLine;
        index = JournalLinesGrid.Items.IndexOf(rowLine);
        return index >= 0;
    }

    private List<DataGridColumn> GetEditableColumns()
    {
        return new List<DataGridColumn>
        {
            AccountCodeColumn,
            DescriptionColumn,
            DebitColumn,
            CreditColumn,
            DepartmentColumn,
            ProjectColumn,
            CostCenterColumn
        };
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
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

    private void MoveFocusToNextZone(bool reverse)
    {
        var focusZones = new List<List<FrameworkElement>>
        {
            new()
            {
                JournalNoTextBoxDesktop,
                JournalNoTextBoxCompact
            },
            new()
            {
                JournalDescriptionTextBoxDesktop,
                JournalDescriptionTextBoxCompact
            },
            new()
            {
                JournalLinesGrid
            },
            new()
            {
                NewJournalButtonDesktop,
                NewJournalButtonCompact,
                SaveDraftButtonDesktop,
                SaveDraftButtonCompact,
                SubmitButtonDesktop,
                SubmitButtonCompact,
                ExportButtonDesktop,
                ExportButtonCompact
            }
        };

        var currentZoneIndex = -1;
        for (var i = 0; i < focusZones.Count; i++)
        {
            if (!focusZones[i].Any(IsElementInCurrentFocusPath))
            {
                continue;
            }

            currentZoneIndex = i;
            break;
        }

        var nextZoneIndex = currentZoneIndex < 0
            ? 0
            : CalculateNextIndex(currentZoneIndex, focusZones.Count, reverse);

        for (var offset = 0; offset < focusZones.Count; offset++)
        {
            var targetZoneIndex = CalculateNextIndex(nextZoneIndex, focusZones.Count, reverse, offset);
            if (TryFocusFirstAvailable(focusZones[targetZoneIndex]))
            {
                return;
            }
        }
    }

    private static int CalculateNextIndex(int index, int count, bool reverse, int step = 1)
    {
        if (count <= 0)
        {
            return 0;
        }

        var delta = reverse ? -step : step;
        var next = (index + delta) % count;
        return next < 0 ? next + count : next;
    }

    private static bool IsElementInCurrentFocusPath(FrameworkElement? element)
    {
        if (element is null || !element.IsVisible)
        {
            return false;
        }

        if (element.IsKeyboardFocusWithin)
        {
            return true;
        }

        var focused = Keyboard.FocusedElement as DependencyObject;
        return focused is not null && IsDescendantOf(focused, element);
    }

    private static bool IsDescendantOf(DependencyObject child, DependencyObject ancestor)
    {
        var current = child;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }

            current = LogicalTreeHelper.GetParent(current) ?? VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private static bool TryFocusFirstAvailable(IEnumerable<FrameworkElement> elements)
    {
        foreach (var element in elements)
        {
            if (element is null || !element.IsVisible || !element.IsEnabled || !element.Focusable)
            {
                continue;
            }

            if (element.Focus())
            {
                return true;
            }
        }

        return false;
    }

    private bool HandleEscapeInGridEditor()
    {
        if (!JournalLinesGrid.IsKeyboardFocusWithin)
        {
            return false;
        }

        var focused = Keyboard.FocusedElement as DependencyObject;
        var cell = FindAncestor<DataGridCell>(focused);
        if (cell is null || !cell.IsEditing)
        {
            return false;
        }

        JournalLinesGrid.CancelEdit(DataGridEditingUnit.Cell);
        JournalLinesGrid.CancelEdit(DataGridEditingUnit.Row);

        if (cell.Focus())
        {
            return true;
        }

        return JournalLinesGrid.Focus();
    }

    private static T? FindAncestor<T>(DependencyObject? start) where T : DependencyObject
    {
        var current = start;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = LogicalTreeHelper.GetParent(current) ?? VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
