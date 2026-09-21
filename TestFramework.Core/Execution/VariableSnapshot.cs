using TestFramework.Abstractions.Models;

namespace TestFramework.Core.Execution;

/// <summary>
/// Point-in-time copies of the variable table for run results.
/// </summary>
internal static class VariableSnapshot
{
    /// <summary>
    /// Copies the variables, duplicating the dictionaries and lists inside them. A shallow copy
    /// shares those containers with the live table, so a plugin mutating one in place would
    /// retroactively change an already-recorded snapshot - including the "before" values a result
    /// is supposed to preserve. Values that are neither dictionaries nor lists are carried over as
    /// they are: they are the plugin's own types and cannot be copied safely from here.
    /// </summary>
    public static Dictionary<string, object?> Capture(IEnumerable<KeyValuePair<string, object?>> variables)
    {
        // The variable table is the framework's own namespace and is case-insensitive by design.
        var snapshot = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in variables)
        {
            snapshot[name] = CaptureValue(value, depth: 0);
        }

        return snapshot;
    }

    private static object? CaptureValue(object? value, int depth)
    {
        // Guards against a self-referencing container, which a plugin is free to build.
        if (depth >= 32)
        {
            return value;
        }

        return value switch
        {
            string => value,
            IDictionary<string, object?> dictionary => CaptureDictionary(dictionary, depth),
            IEnumerable<object?> list => list.Select(item => CaptureValue(item, depth + 1)).ToList(),
            _ => value
        };
    }

    /// <summary>
    /// Copies a dictionary held inside a variable, keeping the key comparer it arrived with - see
    /// <see cref="VariableValue"/>. Forcing case-insensitivity here made a plugin's <c>SOC</c> and
    /// <c>soc</c> collide, and the copy threw rather than merged: an <see cref="ArgumentException"/>
    /// out of the runner that lost the entire run result, raised from a finally block where it
    /// could also replace an in-flight cancellation.
    /// </summary>
    private static Dictionary<string, object?> CaptureDictionary(IDictionary<string, object?> source, int depth)
    {
        var copy = new Dictionary<string, object?>(VariableValue.ComparerOf(source));
        foreach (var (key, value) in source)
        {
            copy[key] = CaptureValue(value, depth + 1);
        }

        return copy;
    }
}
