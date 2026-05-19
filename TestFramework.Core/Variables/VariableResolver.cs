using System.Globalization;
using System.Text.RegularExpressions;

namespace TestFramework.Core.Variables;

public static partial class VariableResolver
{
    public static Dictionary<string, object?> ResolveDictionary(
        IDictionary<string, object?> source,
        IDictionary<string, object?> variables)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in source)
        {
            result[key] = ResolveValue(value, variables);
        }

        return result;
    }

    public static object? ResolveValue(object? value, IDictionary<string, object?> variables)
    {
        return value switch
        {
            string text => ResolveText(text, variables),
            IDictionary<string, object?> dictionary => ResolveDictionary(dictionary, variables),
            IDictionary<object, object?> dictionary => dictionary.ToDictionary(
                pair => Convert.ToString(pair.Key, CultureInfo.InvariantCulture) ?? string.Empty,
                pair => ResolveValue(pair.Value, variables),
                StringComparer.OrdinalIgnoreCase),
            IEnumerable<object?> list => list.Select(item => ResolveValue(item, variables)).ToList(),
            _ => value
        };
    }

    private static object? ResolveText(string text, IDictionary<string, object?> variables)
    {
        var matches = VariablePattern().Matches(text);
        if (matches.Count == 0)
        {
            return text;
        }

        if (matches.Count == 1 && matches[0].Index == 0 && matches[0].Length == text.Length)
        {
            var name = matches[0].Groups["name"].Value;
            return variables.TryGetValue(name, out var value) ? value : text;
        }

        return VariablePattern().Replace(text, match =>
        {
            var name = match.Groups["name"].Value;
            return variables.TryGetValue(name, out var value)
                ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
                : match.Value;
        });
    }

    [GeneratedRegex(@"\$\{(?<name>[A-Za-z_][A-Za-z0-9_.-]*)\}")]
    private static partial Regex VariablePattern();
}
