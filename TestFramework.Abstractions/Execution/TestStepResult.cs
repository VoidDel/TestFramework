using TestFramework.Abstractions.Models;

namespace TestFramework.Abstractions.Execution;

public sealed class TestStepResult
{
    public string StepId { get; set; } = string.Empty;

    public string StepName { get; set; } = string.Empty;

    /// <summary>
    /// The plugin that actually ran, filled in by the runner. The version is the resolved one, not
    /// the one the sequence asked for: when a compatible newer version stood in, this is what makes
    /// the result say which code produced it. Null when no plugin was resolved.
    /// </summary>
    public string? PluginId { get; set; }

    public string? PluginVersion { get; set; }

    /// <summary>
    /// The version the sequence asked for. Differs from <see cref="PluginVersion"/> when a compatible
    /// newer version stood in, which is worth seeing in the result itself rather than only in a log.
    /// </summary>
    public string? RequestedPluginVersion { get; set; }

    /// <summary>How many times the step ran; above one when <c>retry</c> ran it again.</summary>
    public int Attempts { get; set; } = 1;

    /// <summary>
    /// The attempts before this one that ended in Error or Fail, oldest first. Kept so a pass on the
    /// third try reads as one, not as a clean pass; a polling attempt that merely found its condition
    /// not yet true is not kept, or a ten-minute charge poll would carry six hundred of them.
    /// </summary>
    public List<TestStepResult> PreviousAttempts { get; } = [];

    /// <summary>Why the step was skipped - disabled, or its <c>runIf</c> was false. Null otherwise.</summary>
    public string? SkipReason { get; set; }

    public TestVerdict Verdict { get; set; } = TestVerdict.None;

    public string? ErrorMessage { get; set; }

    public Exception? Exception { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset FinishedAt { get; set; }

    public Dictionary<string, object?> Outputs { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, object?> WrittenVariables { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public TimeSpan Duration => FinishedAt - StartedAt;

    public static TestStepResult Skipped(string stepId, string stepName, string? reason = null)
    {
        var now = DateTimeOffset.Now;
        return new TestStepResult
        {
            StepId = stepId,
            StepName = stepName,
            Verdict = TestVerdict.Skipped,
            SkipReason = reason,
            StartedAt = now,
            FinishedAt = now
        };
    }
}
