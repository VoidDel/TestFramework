using System.Collections;
using System.Globalization;

namespace TestFramework.Plugins.Simulation;

/// <summary>
/// A simulated instrument's settings from the station file, read with the types YAML gives them -
/// an unquoted 3.7 arrives as a double, a nested block as a dictionary - and refused with the key's
/// name when a value is not what the key needs.
/// </summary>
internal sealed class SimulationSettings(string alias, IReadOnlyDictionary<string, object?> values)
{
    public string Alias { get; } = alias;

    public double Number(string key, double fallback)
    {
        if (!values.TryGetValue(key, out var value) || value is null)
        {
            return fallback;
        }

        return value switch
        {
            double number when double.IsFinite(number) => number,
            float or int or long or decimal => Convert.ToDouble(value, CultureInfo.InvariantCulture),
            string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => throw Invalid(key, "a number")
        };
    }

    public bool Flag(string key, bool fallback)
    {
        if (!values.TryGetValue(key, out var value) || value is null)
        {
            return fallback;
        }

        return value switch
        {
            bool flag => flag,
            string text when bool.TryParse(text, out var parsed) => parsed,
            _ => throw Invalid(key, "true or false")
        };
    }

    public string? Text(string key) =>
        values.TryGetValue(key, out var value) && value is not null
            ? Convert.ToString(value, CultureInfo.InvariantCulture)
            : null;

    /// <summary>A nested block, such as <c>Inputs: { CH1: fgen }</c>; empty when absent.</summary>
    public IReadOnlyDictionary<string, object?> Map(string key)
    {
        if (!values.TryGetValue(key, out var value) || value is null)
        {
            return new Dictionary<string, object?>();
        }

        if (value is not IDictionary map)
        {
            throw Invalid(key, "a block of key: value pairs");
        }

        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in map)
        {
            result[Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty] = entry.Value;
        }

        return result;
    }

    public string Identity(string model) => Text("Identity") ?? $"TestFramework,{model},SIM-{Alias},1.0";

    private ArgumentException Invalid(string key, string expected) =>
        new($"Simulated instrument '{Alias}': setting '{key}' must be {expected}.");
}
