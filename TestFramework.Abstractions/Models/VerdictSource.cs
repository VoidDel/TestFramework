namespace TestFramework.Abstractions.Models;

public sealed class VerdictSource
{
    public string? StepId { get; set; }

    public string? OutputKey { get; set; }

    public VerdictJudgeType JudgeType { get; set; } = VerdictJudgeType.PassFail;

    public double? LowerLimit { get; set; }

    public double? UpperLimit { get; set; }

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

public enum StringJudgeMode
{
    Exact,
    Regex
}
