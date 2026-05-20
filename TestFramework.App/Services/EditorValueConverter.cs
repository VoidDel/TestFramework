using System.Globalization;

namespace TestFramework.App.Services;

internal static class EditorValueConverter
{
    public static object? Parse(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var trimmed = text.Trim();
        if (trimmed.Contains("${", StringComparison.Ordinal))
        {
            return text;
        }

        if (bool.TryParse(trimmed, out var boolean))
        {
            return boolean;
        }

        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            return integer;
        }

        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }

        return text;
    }

    public static double? ParseNullableDouble(string? text)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    public static string Format(object? value)
    {
        return value switch
        {
            null => string.Empty,
            bool boolean => boolean.ToString(CultureInfo.InvariantCulture).ToLowerInvariant(),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
        };
    }

    public static string FormatNullableDouble(double? value)
    {
        return value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    }

    public static bool IsVariableReference(object? value)
    {
        return value is string text && text.Contains("${", StringComparison.Ordinal);
    }

    public static bool ValuesEqual(object? left, object? right)
    {
        if (Equals(left, right))
        {
            return true;
        }

        return string.Equals(Format(left), Format(right), StringComparison.Ordinal);
    }
}
