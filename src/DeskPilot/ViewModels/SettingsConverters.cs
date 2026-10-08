using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DeskPilot.ViewModels;

/// <summary>true -> Visible, false -> Collapsed (or the reverse with Invert).</summary>
public sealed class SettingsBoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var on = value is true;
        if (Invert) on = !on;
        return on ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility v && (v == Visibility.Visible) != Invert;
}

/// <summary>Non-empty string -> Visible, empty or null -> Collapsed (or the reverse with Invert).</summary>
public sealed class SettingsTextToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var on = value is string s ? s.Length > 0 : value != null;
        if (Invert) on = !on;
        return on ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>
/// Visible when the bound value's text equals the converter parameter (case-insensitive). Several
/// alternatives can be given separated by '|'.
/// </summary>
public sealed class SettingsEqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value?.ToString() ?? "";
        var options = (parameter?.ToString() ?? "").Split('|');
        return options.Any(o => string.Equals(o, text, StringComparison.OrdinalIgnoreCase)) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>
/// For radio buttons bound to an enum (or string) property: checked when the value equals the
/// parameter; checking a radio writes the parameter back. Unchecking writes nothing.
/// </summary>
public sealed class SettingsValueToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true || parameter == null) return Binding.DoNothing;
        var text = parameter.ToString()!;
        var target = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (target.IsEnum)
            return Enum.TryParse(target, text, ignoreCase: true, out var parsed) ? parsed! : Binding.DoNothing;
        return text;
    }
}
