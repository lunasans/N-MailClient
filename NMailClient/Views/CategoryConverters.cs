using System.Globalization;
using System.Windows.Data;
using NMailClient.Models;
using NMailClient.Services;

namespace NMailClient.Views;

/// <summary>Kategorie zu deutschem Namen.</summary>
public class CategoryNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is MailCategory c ? MailCategorizer.DisplayName(c) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>True, wenn Wert und Parameter dieselbe Kategorie sind – für den aktiven Reiter.</summary>
public class CategoryIsSelectedConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
        => values.Length == 2 && values[0] is MailCategory a && values[1] is MailCategory b && a == b;

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
