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

    public Dictionary<string, object?> VariablesAfter { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public TimeSpan Duration => FinishedAt - StartedAt;
}
