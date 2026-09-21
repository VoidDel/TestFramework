using System.Text.RegularExpressions;

namespace TestFramework.Abstractions.Models;

/// <summary>
/// The <c>${name}</c> syntax a parameter value uses to read a sequence variable, and the <c>$${</c>
/// that escapes it.
///
/// One implementation, because four places need it and they have to agree exactly: the resolver
/// that substitutes values at run time, the validator that checks a reference points at a variable
/// that exists, the editors that have to recognise a reference rather than parse it as a number,
/// and the check that warns about text which looks like a reference and is not one. A second copy
/// of this pattern would let a sequence pass validation and fail in the runner, which is the
/// failure this whole check exists to prevent.
/// </summary>
public static partial class VariableReference
{
    /// <summary>
    /// Matches either a reference or an escape. <c>Groups["name"]</c> succeeds for a reference and
    /// does not for an escape, which is how every caller tells the two apart.
    ///
    /// The escape is <c>$$</c> <b>only directly before a brace</b>: <c>$${x}</c> means the literal
    /// text <c>${x}</c>, while <c>$$</c> anywhere else is just two dollar signs and is left alone.
    /// Escaping the whole of <c>$$</c> would have changed the meaning of every value that already
    /// contains one.
    /// </summary>
    [GeneratedRegex(@"\$\$(?=\{)|\$\{(?<name>[A-Za-z_][A-Za-z0-9_.-]*)\}")]
    public static partial Regex Pattern { get; }

    /// <summary>True when the value is a string carrying at least one reference.</summary>
    public static bool IsReference(object? value) =>
        value is string text && Pattern.Matches(text).Any(match => match.Groups["name"].Success);

    /// <summary>The variable names a value refers to; empty for anything that is not a string.</summary>
    public static IReadOnlyList<string> NamesIn(object? value)
    {
        if (value is not string text)
        {
            return [];
        }

        return Pattern.Matches(text)
            .Where(match => match.Groups["name"].Success)
            .Select(match => match.Groups["name"].Value)
            .ToArray();
    }

    /// <summary>
    /// True when the text still holds a <c>${</c> once every reference and escape has been
    /// accounted for - <c>${1stReading}</c>, <c>${my var}</c>, a missing closing brace.
    ///
    /// The resolver leaves these alone and the plugin receives the characters, so nothing
    /// downstream ever objects; and they are exactly the shape a mistyped variable name takes. A
    /// caller that wants to say so needs to be able to tell "meant a reference and got it wrong"
    /// apart from "contains a dollar and a brace on purpose", which is what the escape is for.
    /// </summary>
    public static bool HasMalformedReference(object? value) =>
        value is string text &&
        text.Contains("${", StringComparison.Ordinal) &&
        Pattern.Replace(text, string.Empty).Contains("${", StringComparison.Ordinal);

    /// <summary>
    /// True when the value is exactly one reference and nothing else, which is the case that keeps
    /// the variable's own type instead of becoming text.
    /// </summary>
    public static bool IsWholeValueReference(object? value)
    {
        if (value is not string text)
        {
            return false;
        }

        var matches = Pattern.Matches(text);
        return matches.Count == 1 &&
               matches[0].Index == 0 &&
               matches[0].Length == text.Length &&
               matches[0].Groups["name"].Success;
    }
}
