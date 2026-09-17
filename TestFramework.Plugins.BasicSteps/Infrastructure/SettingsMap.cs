using System.Globalization;

namespace TestFramework.Plugins.BasicSteps.Infrastructure;

internal static class SettingsMap
{
    public static int GetInt(IReadOnlyDictionary<string, object?> map, string key, int defaultValue)
    {
        if (!map.TryGetValue(key, out var value))
        {
            return defaultValue;
        }

        return value switch
        {
            int integer => integer,
            long integer => checked((int)integer),
            double number when double.IsFinite(number) && number == Math.Truncate(number) => checked((int)number),
            string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => throw new ArgumentException($"Parameter '{key}' must be an integer.")
        };
    }

    public static double GetDouble(IReadOnlyDictionary<string, object?> map, string key, double defaultValue)
    {
        if (!map.TryGetValue(key, out var value))
        {
            return defaultValue;
        }

        var result = value switch
        {
            double number => number,
            float number => number,
            int number => number,
            long number => number,
            decimal number => (double)number,
            string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => throw new ArgumentException($"Parameter '{key}' must be a number.")
        };
        return double.IsFinite(result)
            ? result
            : throw new ArgumentException($"Parameter '{key}' must be finite.");
    }

    public static string GetString(IReadOnlyDictionary<string, object?> map, string key, string defaultValue)
    {
        return map.TryGetValue(key, out var value) && value is not null
            ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? defaultValue
            : defaultValue;
    }
}
