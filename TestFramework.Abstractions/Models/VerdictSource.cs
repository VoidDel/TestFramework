namespace TestFramework.Abstractions.Models;

public sealed class VerdictSource
{
    /// <summary>
    /// What the records from this judgment are called - "充电电流" rather than the output key. Null
    /// names them by <see cref="OutputKey"/>, which is what every judgment was called before an
    /// item could hold more than one.
    /// </summary>
    public string? Name { get; set; }

    public string? StepId { get; set; }

    public string? OutputKey { get; set; }

    public VerdictJudgeType JudgeType { get; set; } = VerdictJudgeType.PassFail;

    /// <summary>How a numeric value is compared; see <see cref="NumericComparison"/>.</summary>
    public NumericComparison Comparison { get; set; } = NumericComparison.GELE;

    public double? LowerLimit { get; set; }

    public double? UpperLimit { get; set; }

    /// <summary>
    /// A <c>${variable}</c> standing in for <see cref="LowerLimit"/>, resolved when the item is
    /// judged - after its steps have run, so a limit a step wrote is the one that applies.
    ///
    /// A separate member rather than widening <see cref="LowerLimit"/> to <c>object</c>: plugins
    /// read this type through the execution context, and changing a member's type is the one kind
    /// of change the contract rules out. In a sequence file both are the same <c>lowerLimit</c>
    /// key, holding either a number or a reference; setting both here is a validation error.
    /// </summary>
    public string? LowerLimitReference { get; set; }

    /// <summary>The <see cref="UpperLimit"/> counterpart of <see cref="LowerLimitReference"/>.</summary>
    public string? UpperLimitReference { get; set; }

    /// <summary>The value <see cref="NumericComparison.EQ"/> and <see cref="NumericComparison.NE"/> compare against, in <see cref="Unit"/>.</summary>
    public double? Expected { get; set; }

    /// <summary>The <see cref="Expected"/> counterpart of <see cref="LowerLimitReference"/>.</summary>
    public string? ExpectedReference { get; set; }

    public string? SourceUnit { get; set; }

    public string? Unit { get; set; }

    public StringJudgeMode StringMode { get; set; } = StringJudgeMode.Exact;

    public string? ExpectedString { get; set; }
}

public enum VerdictJudgeType
{
    PassFail,
    Numeric,
    String
}

/// <summary>
/// How a numeric verdict compares the measured value, named by TestStand's comparison codes so a
/// sequence reads the same to anyone who has written one there.
///
/// <see cref="GELE"/> is the default and is what every numeric verdict did before this existed. It
/// keeps that behaviour exactly, including accepting one limit alone - a missing bound is no bound.
/// The other two-limit codes need both: someone who asked for an open interval and wrote one bound
/// has made a mistake, not a one-sided test, and the single-limit codes exist for that.
/// </summary>
public enum NumericComparison
{
    /// <summary>lowerLimit ≤ x ≤ upperLimit.</summary>
    GELE,

    /// <summary>lowerLimit &lt; x &lt; upperLimit.</summary>
    GTLT,

    /// <summary>lowerLimit ≤ x &lt; upperLimit.</summary>
    GELT,

    /// <summary>lowerLimit &lt; x ≤ upperLimit.</summary>
    GTLE,

    /// <summary>x = expected, compared exactly; for integral values such as a cell count or a status code.</summary>
    EQ,

    /// <summary>x ≠ expected, compared exactly.</summary>
    NE,

    /// <summary>x &gt; lowerLimit.</summary>
    GT,

    /// <summary>x ≥ lowerLimit.</summary>
    GE,

    /// <summary>x &lt; upperLimit.</summary>
    LT,

    /// <summary>x ≤ upperLimit.</summary>
    LE
}

public enum StringJudgeMode
{
    Exact,
    Regex
}
