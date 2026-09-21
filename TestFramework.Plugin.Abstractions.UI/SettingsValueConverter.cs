using System.Globalization;
using TestFramework.Abstractions.Models;

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
        if (IsVariableReference(trimmed))
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

    /// <summary>
    /// Whether the value carries a <c>${variable}</c> reference, decided by
    /// <see cref="VariableReference"/> - the same rule the validator and the runner use.
    ///
    /// It used to be a <c>Contains("${")</c> of its own, which answered yes for text the framework
    /// treats as a plain literal: an editor then protected <c>${my var}</c> as a binding while the
    /// runner passed it to the plugin as characters. One pattern, one answer, which is the whole
    /// reason <see cref="VariableReference"/> exists.
    /// </summary>
    public static bool IsVariableReference(object? value) => VariableReference.IsReference(value);

    public static bool ValuesEqual(object? left, object? right)
    {
        if (Equals(left, right))
        {
            return true;
        }

        return string.Equals(Format(left), Format(right), StringComparison.Ordinal);
    }
}
