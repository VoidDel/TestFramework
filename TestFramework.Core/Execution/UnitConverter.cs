using System.Globalization;
using System.Text.RegularExpressions;
using TestFramework.Abstractions.Models;

namespace TestFramework.Core.Execution;

internal static partial class UnitConverter
{
    public static bool TryConvertToDouble(object? value, string? sourceUnit, string? targetUnit, out double converted) =>
        TryConvertToDouble(value, sourceUnit, targetUnit, out converted, out _);

    /// <summary>
    /// As above, also reporting the unit <paramref name="converted"/> is in: the target when one is
    /// declared, otherwise the measurement's own unit or <paramref name="sourceUnit"/>.
    /// </summary>
    public static bool TryConvertToDouble(
        object? value,
        string? sourceUnit,
        string? targetUnit,
        out double converted,
        out string? effectiveUnit)
    {
        converted = default;
        effectiveUnit = null;
        if (!TryReadNumberAndUnit(value, out var number, out var valueUnit))
        {
            return false;
        }

        var fromUnit = string.IsNullOrWhiteSpace(valueUnit) ? sourceUnit : valueUnit;
        effectiveUnit = NullIfBlank(MeasurementUnits.Normalize(string.IsNullOrWhiteSpace(targetUnit) ? fromUnit : targetUnit));
        return TryConvert(number, fromUnit, targetUnit, out converted);
    }

    private static string? NullIfBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

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

    // The table itself, and what counts as the same unit, is MeasurementUnits' - shared with the
    // validator, which warns about a unit nobody knows before the run instead of after it.
    private static bool TryConvert(double value, string? sourceUnit, string? targetUnit, out double converted) =>
        MeasurementUnits.TryConvert(value, sourceUnit, targetUnit, out converted);

    [GeneratedRegex(@"^(?<number>[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?)\s*(?<unit>.*)$")]
    private static partial Regex NumberWithUnitRegex();
}
