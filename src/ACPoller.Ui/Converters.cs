using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace ACPoller.Ui;

/// <summary>Masque un element quand le compte vaut zero.</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int count && count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Inverse un booleen, pour desactiver un bouton pendant une commande.</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool flag && !flag;

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool flag && !flag;
}

/// <summary>
/// Vert pour un succes, rouge pour un echec.
/// </summary>
/// <remarks>
/// Les teintes sont choisies pour rester lisibles sur fond clair ET sur fond
/// sombre : les verts et rouges vifs habituels deviennent illisibles en mode
/// sombre, et c'est precisement la que l'exploitant les regarde.
/// </remarks>
public sealed class BoolToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Success = new(Color.FromRgb(0x2E, 0xA0, 0x43));
    private static readonly SolidColorBrush Failure = new(Color.FromRgb(0xE0, 0x4A, 0x4A));

    static BoolToBrushConverter()
    {
        Success.Freeze();
        Failure.Freeze();
    }

    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Success : Failure;

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
