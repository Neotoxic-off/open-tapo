using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace OpenTapo.Views;

/// <summary>Shows a TextBox placeholder while the box is empty and unfocused. Values: [Text, IsKeyboardFocused].</summary>
public sealed class PlaceholderVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        bool isEmpty = values[0] is not string text || text.Length == 0;
        bool isFocused = values[1] is true;
        return isEmpty && !isFocused ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
