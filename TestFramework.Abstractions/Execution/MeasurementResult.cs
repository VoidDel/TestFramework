using TestFramework.Abstractions.Models;

namespace TestFramework.Abstractions.Execution;

/// <summary>
/// One value an item's verdict was judged on, with what it was judged against.
///
/// The limits and unit are recorded as they stood at judgment time, not left for a report to look
/// up in the sequence: a limit can come from a variable, and the file can be edited after the run.
/// A result that only says "Fail" cannot answer what was measured or which bound it crossed.
///
/// A numeric verdict on a list or dictionary output produces one of these per element - 80 cell
/// voltages read in one step are 80 records, each with its own verdict, so a failure names the
/// cell.
/// </summary>
public sealed class MeasurementResult
{
    /// <summary>
    /// The output key for a single value; <c>key[index]</c> for a list element; the element's own
    /// key for a dictionary element, which is usually the signal name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The judgment this record came from: its name, or its output key when it has none. Keeps two
    /// checks' records apart when their element names match - both dictionaries keyed by cell.
    /// </summary>
    public string Check { get; set; } = string.Empty;

    /// <summary>The comparison a numeric record was judged by; null for a string or pass/fail judgment.</summary>
    public NumericComparison? Comparison { get; set; }

    /// <summary>The value <see cref="NumericComparison.EQ"/> or <see cref="NumericComparison.NE"/> compared against.</summary>
    public double? Expected { get; set; }

    /// <summary>Position within a list or dictionary output; null for a single value.</summary>
    public int? Index { get; set; }

    /// <summary>The value exactly as the step produced it.</summary>
    public object? RawValue { get; set; }

    /// <summary>The number compared against the limits, in <see cref="Unit"/>; null when there was none.</summary>
    public double? Value { get; set; }

    /// <summary>The unit <see cref="Value"/> and the limits are in, when one is known.</summary>
    public string? Unit { get; set; }

    public double? LowerLimit { get; set; }

    public double? UpperLimit { get; set; }

    public string? ExpectedString { get; set; }

    public TestVerdict Verdict { get; set; } = TestVerdict.None;
}
