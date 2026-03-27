using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Accounting.ViewModels;

namespace Accounting.Views.Components;

public partial class UserManagementUsersTabControl : UserControl
{
    public UserManagementUsersTabControl()
    {
        InitializeComponent();
    }

    private void AccessAuditMatrixGrid_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is DataGrid grid)
        {
            ConfigureAccessAuditMatrixGrid(grid);
        }
    }

    private void AccessAuditMatrixGrid_OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is DataGrid grid && grid.IsLoaded)
        {
            ConfigureAccessAuditMatrixGrid(grid);
        }
    }

    private void ConfigureAccessAuditMatrixGrid(DataGrid grid)
    {
        if (grid.Tag is not AccessAuditModuleGroup module)
        {
            return;
        }

        var layout = DetermineMatrixLayout(module.MatrixColumns.Count);

        while (grid.Columns.Count > 1)
        {
            grid.Columns.RemoveAt(grid.Columns.Count - 1);
        }

        foreach (var column in module.MatrixColumns)
        {
            grid.Columns.Add(CreateAuditMatrixColumn(column.ActionCode, column.Header, layout));
        }
    }

    private DataGridTemplateColumn CreateAuditMatrixColumn(string actionCode, string header, MatrixLayout layout)
    {
        var groupKey = GetActionGroup(header);
        var band = ResolveMatrixBand(groupKey);

        return new DataGridTemplateColumn
        {
            Header = CreateCompactHeader(header, groupKey, layout, band),
            Width = new DataGridLength(layout.ColumnWidth),
            CellTemplate = BuildAuditMatrixCellTemplate(actionCode, layout),
            CellStyle = CreateMatrixCellStyle(band)
        };
    }

    private Border CreateCompactHeader(string header, string groupKey, MatrixLayout layout, MatrixBand band)
    {
        var stackPanel = new StackPanel();

        stackPanel.Children.Add(new TextBlock
        {
            Text = groupKey,
            FontSize = 8,
            Foreground = band.Foreground,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center
        });

        stackPanel.Children.Add(new TextBlock
        {
            Text = ToCompactHeaderLabel(header, layout.HeaderLength),
            FontSize = layout.HeaderFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = band.Foreground,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });

        return new Border
        {
            Padding = new Thickness(4, 2, 4, 2),
            CornerRadius = new CornerRadius(8),
            Background = band.HeaderBackground,
            BorderBrush = band.BorderBrush,
            BorderThickness = new Thickness(1),
            Child = stackPanel,
            ToolTip = $"{header}\nKategori: {groupKey}"
        };
    }

    private static string ToCompactHeaderLabel(string header, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(header) || header.Length <= maxLength)
        {
            return header;
        }

        var parts = header
            .Split(['_', '-', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length > 1)
        {
            var compact = string.Concat(parts.Select(part => char.ToUpperInvariant(part[0])));
            if (compact.Length >= 2)
            {
                return compact[..Math.Min(compact.Length, maxLength)];
            }
        }

        return header[..maxLength].ToUpperInvariant();
    }

    private static MatrixLayout DetermineMatrixLayout(int actionColumnCount)
    {
        return actionColumnCount switch
        {
            >= 12 => new MatrixLayout(52, 3, 9, 16, 10),
            >= 8 => new MatrixLayout(64, 5, 10, 20, 12),
            _ => new MatrixLayout(84, 12, 10, 24, 12)
        };
    }

    private DataTemplate BuildAuditMatrixCellTemplate(string actionCode, MatrixLayout layout)
    {
        var boolToVisibility = TryFindResource("BoolToVisibilityConverter");
        var inverseBoolToVisibility = TryFindResource("InverseBoolToVisibilityConverter");

        var gridFactory = new FrameworkElementFactory(typeof(Grid));

        var badgeFactory = new FrameworkElementFactory(typeof(Border));
        badgeFactory.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        badgeFactory.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        badgeFactory.SetValue(Border.PaddingProperty, new Thickness(0));
        badgeFactory.SetValue(Border.WidthProperty, layout.BadgeSize);
        badgeFactory.SetValue(Border.HeightProperty, layout.BadgeSize);
        badgeFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(layout.BadgeSize / 2));
        badgeFactory.SetValue(Border.BackgroundProperty, TryFindResource("Brush.PrimarySubtle"));
        badgeFactory.SetBinding(ToolTipProperty, new Binding($"Cells[{actionCode}].Tooltip"));
        badgeFactory.SetBinding(VisibilityProperty, new Binding($"Cells[{actionCode}].IsAvailable")
        {
            Converter = boolToVisibility as IValueConverter
        });

        var badgeTextFactory = new FrameworkElementFactory(typeof(TextBlock));
        badgeTextFactory.SetValue(TextBlock.TextProperty, "✓");
        badgeTextFactory.SetValue(TextBlock.FontSizeProperty, layout.BadgeFontSize);
        badgeTextFactory.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        badgeTextFactory.SetValue(TextBlock.ForegroundProperty, TryFindResource("Brush.Success"));
        badgeTextFactory.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        badgeTextFactory.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        badgeFactory.AppendChild(badgeTextFactory);

        var placeholderFactory = new FrameworkElementFactory(typeof(TextBlock));
        placeholderFactory.SetValue(TextBlock.TextProperty, "-");
        placeholderFactory.SetValue(TextBlock.FontSizeProperty, 11d);
        placeholderFactory.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        placeholderFactory.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        placeholderFactory.SetValue(TextBlock.ForegroundProperty, TryFindResource("Brush.TextMuted"));
        placeholderFactory.SetBinding(VisibilityProperty, new Binding($"Cells[{actionCode}].IsAvailable")
        {
            Converter = inverseBoolToVisibility as IValueConverter
        });

        gridFactory.AppendChild(badgeFactory);
        gridFactory.AppendChild(placeholderFactory);

        return new DataTemplate
        {
            VisualTree = gridFactory
        };
    }

    private Style CreateMatrixCellStyle(MatrixBand band)
    {
        var style = new Style(typeof(DataGridCell));
        style.Setters.Add(new Setter(BackgroundProperty, band.CellBackground));
        style.Setters.Add(new Setter(BorderBrushProperty, TryFindResource("Brush.BorderSoft")));
        style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
        return style;
    }

    private static string GetActionGroup(string header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return "-";
        }

        var parts = header
            .Split(['_', '-', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var raw = parts.FirstOrDefault() ?? header;
        return raw[..Math.Min(raw.Length, 3)].ToUpperInvariant();
    }

    private MatrixBand ResolveMatrixBand(string groupKey)
    {
        return (Math.Abs(StringComparer.OrdinalIgnoreCase.GetHashCode(groupKey)) % 4) switch
        {
            0 => new MatrixBand(
                GetBrushResource("Brush.PrimarySubtle"),
                GetBrushResource("Brush.Primary"),
                GetBrushResource("Brush.PrimarySubtle"),
                GetBrushResource("Brush.Primary")),
            1 => new MatrixBand(
                GetBrushResource("Brush.InfoSubtle"),
                GetBrushResource("Brush.Info"),
                GetBrushResource("Brush.InfoSubtle"),
                GetBrushResource("Brush.Info")),
            2 => new MatrixBand(
                GetBrushResource("Brush.SurfaceAlt"),
                GetBrushResource("Brush.TextPrimary"),
                GetBrushResource("Brush.SurfaceAlt"),
                GetBrushResource("Brush.BorderStrong")),
            _ => new MatrixBand(
                GetBrushResource("Brush.SurfaceMuted"),
                GetBrushResource("Brush.TextSecondary"),
                GetBrushResource("Brush.SurfaceMuted"),
                GetBrushResource("Brush.BorderSoft"))
        };
    }

    private Brush GetBrushResource(string key)
    {
        return (Brush)(TryFindResource(key) ?? Brushes.Transparent);
    }

    private readonly record struct MatrixLayout(
        double ColumnWidth,
        int HeaderLength,
        double HeaderFontSize,
        double BadgeSize,
        double BadgeFontSize);

    private readonly record struct MatrixBand(Brush HeaderBackground, Brush Foreground, Brush CellBackground, Brush BorderBrush);
}
