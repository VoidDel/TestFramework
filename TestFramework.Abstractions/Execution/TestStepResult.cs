using TestFramework.Abstractions.Models;

namespace TestFramework.Abstractions.Execution;

public sealed class TestStepResult
{
    public string StepId { get; set; } = string.Empty;

    public string StepName { get; set; } = string.Empty;

    public TestVerdict Verdict { get; set; } = TestVerdict.None;

    public string? ErrorMessage { get; set; }

    public Exception? Exception { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset FinishedAt { get; set; }

    public Dictionary<string, object?> Outputs { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, object?> WrittenVariables { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public TimeSpan Duration => FinishedAt - StartedAt;

    public static TestStepResult Skipped(string stepId, string stepName)
    {
        var now = DateTimeOffset.Now;
        return new TestStepResult
        {
            StepId = stepId,
            StepName = stepName,
            Verdict = TestVerdict.Skipped,
            StartedAt = now,
            FinishedAt = now
        };
    }
}
