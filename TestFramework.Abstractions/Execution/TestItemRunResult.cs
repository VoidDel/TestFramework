using TestFramework.Abstractions.Models;

namespace TestFramework.Abstractions.Execution;

public sealed class TestItemRunResult
{
    public string ItemId { get; set; } = string.Empty;

    public string ItemName { get; set; } = string.Empty;

    public TestVerdict Verdict { get; set; } = TestVerdict.None;

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset FinishedAt { get; set; }

    public List<TestStepResult> InitResults { get; } = [];

    public List<TestStepResult> MainResults { get; } = [];

    public List<TestStepResult> CleanupResults { get; } = [];

    public TestStepResult? VerdictSourceStepResult { get; set; }

    /// <summary>
    /// The values the verdict was judged on, when <c>verdictSource.outputKey</c> is set: one for a
    /// single value, one per element for a list or dictionary output. Empty when the item took the
    /// verdict source step's own verdict, or was never judged.
    /// </summary>
    public List<MeasurementResult> Measurements { get; } = [];

    /// <summary>
    /// Why the item is <c>Error</c> when no step says so - a configured output that was not
    /// produced, or a limit variable that does not hold a number. Null otherwise; a step's own
    /// error stays on that step.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>The loop index this result is for; null when the item does not loop.</summary>
    public int? Iteration { get; set; }

    /// <summary>Which attempt this is; above one when <c>retry</c> ran the item again.</summary>
    public int Attempt { get; set; } = 1;

    /// <summary>The attempts before this one, oldest first, each with its own steps and measurements.</summary>
    public List<TestItemRunResult> PreviousAttempts { get; } = [];

    /// <summary>The results of a group's items, or of a called sequence's items.</summary>
    public List<TestItemRunResult> Children { get; } = [];

    /// <summary>For a call: the sequence that ran, by id and by the path it was called with.</summary>
    public string? CalledSequenceId { get; set; }

    public string? CalledSequencePath { get; set; }

    /// <summary>Why the item was skipped - disabled, or its <c>runIf</c> was false. Null otherwise.</summary>
    public string? SkipReason { get; set; }

    public Dictionary<string, object?> VariablesAfter { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public TimeSpan Duration => FinishedAt - StartedAt;
}
