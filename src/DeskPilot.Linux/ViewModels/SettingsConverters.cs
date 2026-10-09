using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace DeskPilot.Linux.ViewModels;

/// <summary>
/// True when the bound value's text equals the converter parameter (case-insensitive). Several
/// alternatives can be given separated by '|'. Used to show one settings page at a time.
/// </summary>
public sealed class SettingsEqualsConverter : IValueConverter
{
    public static readonly SettingsEqualsConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value?.ToString() ?? "";
        var options = (parameter?.ToString() ?? "").Split('|');
        return options.Any(o => string.Equals(o, text, StringComparison.OrdinalIgnoreCase));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BindingOperations.DoNothing;
}

/// <summary>
/// For radio buttons bound to an enum (or string) property: checked when the value equals the
/// parameter; checking a radio writes the parameter back. Unchecking writes nothing, so the radio
/// that loses its check in a group never overwrites the value the other one just set.
/// </summary>
public sealed class SettingsValueToBoolConverter : IValueConverter
{
    public static readonly SettingsValueToBoolConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true || parameter == null) return BindingOperations.DoNothing;
        var text = parameter.ToString()!;
        var target = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (target.IsEnum)
            return Enum.TryParse(target, text, ignoreCase: true, out var parsed) ? parsed! : BindingOperations.DoNothing;
        return text;
    }
}
