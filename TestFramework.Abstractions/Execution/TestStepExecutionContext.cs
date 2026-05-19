using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Resources;

namespace TestFramework.Abstractions.Execution;

public sealed class TestStepExecutionContext
{
    public required TestSequence Sequence { get; init; }

    public required TestItemDefinition Item { get; init; }

    public required TestStepDefinition Step { get; init; }

    public required IDictionary<string, object?> Variables { get; init; }

    public IReadOnlyDictionary<string, object?> ResolvedParameters { get; init; } =
        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

    public required IReadOnlyDictionary<string, TestStepResult> PreviousStepResults { get; init; }

    public IInstrumentProvider Instruments { get; init; } = RuntimeResourceProvider.Empty;

    public ITransportProvider Transports { get; init; } = RuntimeResourceProvider.Empty;

    public ITestServiceProvider Services { get; init; } = RuntimeResourceProvider.Empty;

    public Action<string>? Log { get; init; }
}
