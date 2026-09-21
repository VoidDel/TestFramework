using System.Text.RegularExpressions;

namespace TestFramework.Abstractions.Models;

/// <summary>
/// The <c>${name}</c> syntax a parameter value uses to read a sequence variable.
///
/// One implementation, because three places need it and they have to agree exactly: the resolver
/// that substitutes values at run time, the validator that checks a reference points at a variable
/// that exists, and the editors that have to recognise a reference rather than parse it as a
/// number. A second copy of this pattern would let a sequence pass validation and fail in the
/// runner, which is the failure this whole check exists to prevent.
/// </summary>
public static partial class VariableReference
{
    [GeneratedRegex(@"\$\{(?<name>[A-Za-z_][A-Za-z0-9_.-]*)\}")]
    public static partial Regex Pattern { get; }

    /// <summary>True when the value is a string carrying at least one reference.</summary>
    public static bool IsReference(object? value) =>
        value is string text && Pattern.IsMatch(text);

    /// <summary>The variable names a value refers to; empty for anything that is not a string.</summary>
    public static IReadOnlyList<string> NamesIn(object? value)
    {
        if (value is not string text)
        {
            return [];
        }

        var matches = Pattern.Matches(text);
        if (matches.Count == 0)
        {
            return [];
        }

        return matches.Select(match => match.Groups["name"].Value).ToArray();
    }
}
