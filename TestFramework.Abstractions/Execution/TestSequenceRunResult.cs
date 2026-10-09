using TestFramework.Abstractions.Models;

namespace TestFramework.Abstractions.Execution;

public sealed class TestSequenceRunResult
{
    public string SequenceId { get; set; } = string.Empty;

    public string SequenceName { get; set; } = string.Empty;

    /// <summary>The sequence's own <c>version</c>, as its authors numbered it.</summary>
    public string? SequenceVersion { get; set; }

    /// <summary>The unit, operator, station and sequence file the host ran this for; see <see cref="TestRunInfo"/>.</summary>
    public TestRunInfo RunInfo { get; set; } = new();

    /// <summary>The framework build that ran it - <c>TestFramework.Core</c>'s informational version.</summary>
    public string? FrameworkVersion { get; set; }

    /// <summary>The plugin contract that build provides; see <c>FrameworkContract</c>.</summary>
    public string? FrameworkContractVersion { get; set; }

    public TestVerdict Verdict { get; set; } = TestVerdict.None;

    // True when an in-process plugin was still executing when this result was returned.
    public bool HasPendingExecution { get; set; }

    public List<string> ResourceErrors { get; } = [];

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset FinishedAt { get; set; }

    public List<TestItemRunResult> ItemResults { get; } = [];

    public Dictionary<string, object?> InitialVariables { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, object?> FinalVariables { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public TimeSpan Duration => FinishedAt - StartedAt;
}
