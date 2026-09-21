using System.Globalization;
using TestFramework.Abstractions.Models;

namespace TestFramework.Core.Variables;

public static class VariableResolver
{
    /// <summary>
    /// Resolves a step's parameter dictionary. Its keys are the framework's own namespace, matched
    /// case-insensitively the way <c>StepParameterDescriptor.Name</c> says they are; dictionaries
    /// nested inside the values are the plugin's data and keep their own comparer.
    /// </summary>
    public static Dictionary<string, object?> ResolveDictionary(
        IDictionary<string, object?> source,
        IDictionary<string, object?> variables)
    {
        return Resolve(source, variables, StringComparer.OrdinalIgnoreCase);
    }

    public static object? ResolveValue(object? value, IDictionary<string, object?> variables)
    {
        return value switch
        {
            string text => ResolveText(text, variables),
            // Nested: see VariableValue - widening these to case-insensitive silently merged two
            // distinct keys of the plugin's own data and dropped one of them.
            IDictionary<string, object?> dictionary => Resolve(dictionary, variables, VariableValue.ComparerOf(dictionary)),
            IDictionary<object, object?> dictionary => ResolveObjectKeyed(dictionary, variables),
            IEnumerable<object?> list => list.Select(item => ResolveValue(item, variables)).ToList(),
            _ => value
        };
    }

    private static Dictionary<string, object?> Resolve(
        IDictionary<string, object?> source,
        IDictionary<string, object?> variables,
        IEqualityComparer<string> comparer)
    {
        var result = new Dictionary<string, object?>(comparer);
        foreach (var (key, value) in source)
        {
            result[key] = ResolveValue(value, variables);
        }

        return result;
    }

    private static Dictionary<string, object?> ResolveObjectKeyed(
        IDictionary<object, object?> source,
        IDictionary<string, object?> variables)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in source)
        {
            result[Convert.ToString(key, CultureInfo.InvariantCulture) ?? string.Empty] = ResolveValue(value, variables);
        }

        return result;
    }

    private static object? ResolveText(string text, IDictionary<string, object?> variables)
    {
        var matches = VariableReference.Pattern.Matches(text);
        if (matches.Count == 0)
        {
            return text;
        }

        if (matches.Count == 1 && matches[0].Index == 0 && matches[0].Length == text.Length)
        {
            var name = matches[0].Groups["name"].Value;
            return GetRequiredVariable(name, variables);
        }

        return VariableReference.Pattern.Replace(text, match =>
        {
            var name = match.Groups["name"].Value;
            return Convert.ToString(GetRequiredVariable(name, variables), CultureInfo.InvariantCulture) ?? string.Empty;
        });
    }

    private static object? GetRequiredVariable(string name, IDictionary<string, object?> variables)
    {
        return variables.TryGetValue(name, out var value)
            ? value
            : throw new InvalidOperationException($"Variable '{name}' is not defined.");
    }
}
