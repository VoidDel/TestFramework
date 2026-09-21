using System.Globalization;
using TestFramework.Abstractions.Models;

namespace TestFramework.Core.Variables;

public static class VariableResolver
{
    /// <summary>
    /// Matches the nesting the sequence loader accepts, so anything that loads from a file also
    /// resolves. The guard is not for files, though: a plugin's <c>SaveSettings</c> decides what a
    /// host writes into <c>TestStepDefinition.Parameters</c>, and a structure that contains itself
    /// would recurse here until the stack ran out - which cannot be caught and takes the process,
    /// and the DUT, down with it.
    /// </summary>
    private const int MaxNestingDepth = 64;

    /// <summary>
    /// Resolves a step's parameter dictionary. Its keys are the framework's own namespace, matched
    /// case-insensitively the way <c>StepParameterDescriptor.Name</c> says they are; dictionaries
    /// nested inside the values are the plugin's data and keep their own comparer.
    /// </summary>
    public static Dictionary<string, object?> ResolveDictionary(
        IDictionary<string, object?> source,
        IDictionary<string, object?> variables)
    {
        return Resolve(source, variables, StringComparer.OrdinalIgnoreCase, depth: 0);
    }

    public static object? ResolveValue(object? value, IDictionary<string, object?> variables) =>
        ResolveValue(value, variables, depth: 0);

    private static object? ResolveValue(object? value, IDictionary<string, object?> variables, int depth)
    {
        if (depth > MaxNestingDepth)
        {
            throw new InvalidOperationException(
                $"Parameter nesting is deeper than the supported limit of {MaxNestingDepth} levels.");
        }

        return value switch
        {
            string text => ResolveText(text, variables),
            // Nested: see VariableValue - widening these to case-insensitive silently merged two
            // distinct keys of the plugin's own data and dropped one of them.
            IDictionary<string, object?> dictionary =>
                Resolve(dictionary, variables, VariableValue.ComparerOf(dictionary), depth + 1),
            IDictionary<object, object?> dictionary => ResolveObjectKeyed(dictionary, variables, depth + 1),
            IEnumerable<object?> list => list.Select(item => ResolveValue(item, variables, depth + 1)).ToList(),
            _ => value
        };
    }

    private static Dictionary<string, object?> Resolve(
        IDictionary<string, object?> source,
        IDictionary<string, object?> variables,
        IEqualityComparer<string> comparer,
        int depth)
    {
        var result = new Dictionary<string, object?>(comparer);
        foreach (var (key, value) in source)
        {
            result[key] = ResolveValue(value, variables, depth + 1);
        }

        return result;
    }

    private static Dictionary<string, object?> ResolveObjectKeyed(
        IDictionary<object, object?> source,
        IDictionary<string, object?> variables,
        int depth)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in source)
        {
            result[Convert.ToString(key, CultureInfo.InvariantCulture) ?? string.Empty] =
                ResolveValue(value, variables, depth + 1);
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

        // One reference and nothing else: the variable keeps its own type, so a numeric variable
        // reaches the plugin as a number rather than as its text.
        if (VariableReference.IsWholeValueReference(text))
        {
            return GetRequiredVariable(matches[0].Groups["name"].Value, variables);
        }

        return VariableReference.Pattern.Replace(text, match =>
        {
            // No name group means this is the "$${" escape: the author wanted a literal "${".
            if (!match.Groups["name"].Success)
            {
                return "$";
            }

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
