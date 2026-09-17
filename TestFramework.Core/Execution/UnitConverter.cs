using System.Globalization;
using System.Text.RegularExpressions;

namespace TestFramework.Core.Execution;

internal static partial class UnitConverter
{
    private static readonly Dictionary<string, UnitDefinition> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        [""] = new("", 1.0),
        ["V"] = new("voltage", 1.0),
        ["mV"] = new("voltage", 0.001),
        ["uV"] = new("voltage", 0.000001),
        ["A"] = new("current", 1.0),
        ["mA"] = new("current", 0.001),
        ["uA"] = new("current", 0.000001),
        ["Ohm"] = new("resistance", 1.0),
        ["kOhm"] = new("resistance", 1000.0),
        ["MOhm"] = new("resistance", 1000000.0),
        ["Hz"] = new("frequency", 1.0),
        ["kHz"] = new("frequency", 1000.0),
        ["MHz"] = new("frequency", 1000000.0),
        ["s"] = new("time", 1.0),
        ["ms"] = new("time", 0.001),
        ["us"] = new("time", 0.000001),
        ["C"] = new("temperature-celsius", 1.0),
        ["%"] = new("ratio", 1.0)
    };

    public static bool TryConvertToDouble(object? value, string? sourceUnit, string? targetUnit, out double converted)
    {
        converted = default;
        if (!TryReadNumberAndUnit(value, out var number, out var valueUnit))
        {
            return false;
        }

        var fromUnit = string.IsNullOrWhiteSpace(valueUnit) ? sourceUnit : valueUnit;
        return TryConvert(number, fromUnit, targetUnit, out converted);
    }

    private static bool TryReadNumberAndUnit(object? value, out double number, out string? unit)
    {
        unit = null;
        switch (value)
        {
            case null:
                number = default;
                return false;
            case double doubleValue:
                number = doubleValue;
                return true;
            case float floatValue:
                number = floatValue;
                return true;
            case decimal decimalValue:
                number = (double)decimalValue;
                return true;
            case int intValue:
                number = intValue;
                return true;
            case long longValue:
                number = longValue;
                return true;
            case string text:
                return TryReadString(text, out number, out unit);
            case IConvertible convertible:
                try
                {
                    number = convertible.ToDouble(CultureInfo.InvariantCulture);
                    return true;
                }
                catch (FormatException)
                {
                    number = default;
                    return false;
                }
                catch (InvalidCastException)
                {
                    number = default;
                    return false;
                }
            default:
                number = default;
                return false;
        }
    }

    private static bool TryReadString(string text, out double number, out string? unit)
    {
        number = default;
        unit = null;
        var match = NumberWithUnitRegex().Match(text.Trim());
        if (!match.Success)
        {
            return false;
        }

        if (!double.TryParse(match.Groups["number"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
        {
            return false;
        }

        unit = match.Groups["unit"].Value.Trim();
        return true;
    }

    private static bool TryConvert(double value, string? sourceUnit, string? targetUnit, out double converted)
    {
        converted = value;
        var source = NormalizeUnit(sourceUnit);
        var target = NormalizeUnit(targetUnit);
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(target))
        {
            return true;
        }

        if (!Units.TryGetValue(source, out var sourceDefinition) ||
            !Units.TryGetValue(target, out var targetDefinition) ||
            !string.Equals(sourceDefinition.Dimension, targetDefinition.Dimension, StringComparison.Ordinal))
        {
            return false;
        }

        converted = value * sourceDefinition.ScaleToBase / targetDefinition.ScaleToBase;
        return true;
    }

    private static string NormalizeUnit(string? unit)
    {
        return unit?.Trim()
            .Replace("Ω", "Ohm", StringComparison.Ordinal)
            .Replace("μ", "u", StringComparison.Ordinal)
            // U+00B5 MICRO SIGN is what keyboards and most editors produce for "uV"/"uA"/"us";
            // it is a different code point from U+03BC GREEK SMALL LETTER MU above.
            .Replace("µ", "u", StringComparison.Ordinal) ?? string.Empty;
    }

    [GeneratedRegex(@"^(?<number>[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?)\s*(?<unit>.*)$")]
    private static partial Regex NumberWithUnitRegex();

    private sealed record UnitDefinition(string Dimension, double ScaleToBase);
}
