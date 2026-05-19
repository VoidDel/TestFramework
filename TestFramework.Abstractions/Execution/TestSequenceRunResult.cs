using TestFramework.Abstractions.Models;

namespace TestFramework.Abstractions.Execution;

public sealed class TestSequenceRunResult
{
    public string SequenceId { get; set; } = string.Empty;

    public string SequenceName { get; set; } = string.Empty;

    public TestVerdict Verdict { get; set; } = TestVerdict.None;

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset FinishedAt { get; set; }

    public List<TestItemRunResult> ItemResults { get; } = [];

    public Dictionary<string, object?> InitialVariables { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, object?> FinalVariables { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public TimeSpan Duration => FinishedAt - StartedAt;
}
