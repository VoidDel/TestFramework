using System.Globalization;

namespace TestFramework.Plugin.Abstractions.UI;

/// <summary>
/// Converts between parameter values and the text an editor shows.
///
/// It lives in the editor SDK rather than the host because a plugin's editor has to read and write
/// parameters exactly the way the framework does - same number formats, same handling of
/// <c>${variable}</c> references - or a value round-trips through the editor and comes back changed.
/// </summary>
public static class SettingsValueConverter
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

    public static bool TryParseNullableDouble(string? text, out double? value)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            value = null;
            return true;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed))
        {
            value = parsed;
            return true;
        }

        value = null;
        return false;
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
