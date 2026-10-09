namespace TestFramework.Abstractions.Models;

/// <summary>
/// Which bounds each <see cref="NumericComparison"/> reads, and the comparison itself.
///
/// One implementation for the validator, the runner and a host's editor, so "this comparison needs
/// an upper limit" means the same before a run as during one, and a form never offers a field the
/// runner ignores.
/// </summary>
public static class NumericComparisonRules
{
    public static bool UsesLowerLimit(this NumericComparison comparison) =>
        comparison is NumericComparison.GELE or NumericComparison.GTLT or NumericComparison.GELT
            or NumericComparison.GTLE or NumericComparison.GT or NumericComparison.GE;

    public static bool UsesUpperLimit(this NumericComparison comparison) =>
        comparison is NumericComparison.GELE or NumericComparison.GTLT or NumericComparison.GELT
            or NumericComparison.GTLE or NumericComparison.LT or NumericComparison.LE;

    public static bool UsesExpected(this NumericComparison comparison) =>
        comparison is NumericComparison.EQ or NumericComparison.NE;

    /// <summary>
    /// True for the interval codes that need both limits. <see cref="NumericComparison.GELE"/> is
    /// not one: it predates the others and has always accepted a single bound.
    /// </summary>
    public static bool RequiresBothLimits(this NumericComparison comparison) =>
        comparison is NumericComparison.GTLT or NumericComparison.GELT or NumericComparison.GTLE;

    /// <summary>
    /// Whether <paramref name="value"/> passes. A bound the comparison uses but that is absent is
    /// treated as no bound, which only <see cref="NumericComparison.GELE"/> can reach - the runner
    /// refuses every other comparison with a bound missing before it gets here.
    /// </summary>
    public static bool Passes(this NumericComparison comparison, double value, double? lower, double? upper, double? expected)
    {
        return comparison switch
        {
            NumericComparison.GELE => (lower is not { } gele || value >= gele) && (upper is not { } lele || value <= lele),
            NumericComparison.GTLT => value > lower && value < upper,
            NumericComparison.GELT => value >= lower && value < upper,
            NumericComparison.GTLE => value > lower && value <= upper,
            NumericComparison.EQ => value == expected,
            NumericComparison.NE => expected is { } ne && value != ne,
            NumericComparison.GT => value > lower,
            NumericComparison.GE => value >= lower,
            NumericComparison.LT => value < upper,
            NumericComparison.LE => value <= upper,
            _ => throw new ArgumentOutOfRangeException(nameof(comparison), comparison, "Unknown numeric comparison.")
        };
    }
}
