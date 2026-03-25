using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Accounting.Services;

namespace Accounting.Converters;

public sealed class EnumToBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not ThemeMode mode || parameter is null)
        {
            return false;
        }

        return string.Equals(mode.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not bool isChecked || !isChecked || parameter is null)
        {
            return Binding.DoNothing;
        }

        return Enum.TryParse(typeof(ThemeMode), parameter.ToString(), true, out var parsed)
            ? parsed
            : Binding.DoNothing;
    }
}

