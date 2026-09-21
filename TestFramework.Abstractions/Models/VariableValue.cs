namespace TestFramework.Abstractions.Models;

/// <summary>
/// How the framework copies a dictionary that lives inside a variable or a parameter value.
///
/// The variable table, a step's parameter dictionary and an instrument's settings block are
/// namespaces the framework owns, and it matches their keys case-insensitively on purpose. A
/// dictionary found <i>inside</i> one of those values is not one of them: it is the plugin's data -
/// a CAN signal map, a decoded frame, a JSON payload - where <c>SOC</c> and <c>soc</c> are two
/// signals, not one written twice. Rebuilding it case-insensitively changes what the plugin's own
/// data means.
///
/// Four places copy these values - result snapshots, variable resolution, YAML loading and step
/// duplication - and each one used to force <see cref="StringComparer.OrdinalIgnoreCase"/> on every
/// nested dictionary. Depending on which copier was reached that either threw (an
/// <see cref="ArgumentException"/> out of a run, losing the whole result) or silently merged the two
/// entries and dropped one. The rule lives here so the four cannot drift apart again.
/// </summary>
public static class VariableValue
{
    /// <summary>
    /// The comparer a copy of <paramref name="source"/> should use: the one it already has, or the
    /// case-sensitive default when it does not expose one.
    ///
    /// It never widens a case-sensitive dictionary into a case-insensitive one, because that is the
    /// direction in which entries disappear. A caller that owns the namespace - the variable table
    /// itself, say - passes its own comparer instead of asking here.
    /// </summary>
    public static IEqualityComparer<string> ComparerOf(IDictionary<string, object?> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return (source as Dictionary<string, object?>)?.Comparer ?? StringComparer.Ordinal;
    }

    /// <summary>
    /// True when <paramref name="keys"/> holds two entries that a case-insensitive namespace cannot
    /// tell apart, with <paramref name="collision"/> set to the second one.
    ///
    /// Used where such a pair is genuinely ambiguous rather than merely case-sensitive data: the
    /// top-level <c>variables</c>, <c>parameters</c> and <c>settings</c> blocks of a sequence file,
    /// which the framework reads case-insensitively. Merging them silently would decide, by file
    /// order, which of the operator's two values survives.
    /// </summary>
    public static bool TryFindCaseInsensitiveCollision(IEnumerable<string> keys, out string collision)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            if (!seen.Add(key))
            {
                collision = key;
                return true;
            }
        }

        collision = string.Empty;
        return false;
    }
}
