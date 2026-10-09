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

    public IInstrumentProvider Instruments { get; init; } = EmptyResourceScope.Instance;

    public ITransportProvider Transports { get; init; } = EmptyResourceScope.Instance;

    public ITestServiceProvider Services { get; init; } = EmptyResourceScope.Instance;

    public Action<string>? Log { get; init; }

    /// <summary>
    /// Asks the operator; see <see cref="IOperatorInteraction"/>. Requires framework contract 1.1:
    /// a plugin using it declares <c>[assembly: TestFrameworkPlugin("1.1")]</c>, so a 1.0 host
    /// refuses it at load instead of failing with a missing member mid-run.
    /// </summary>
    public IOperatorInteraction Operator { get; init; } = NoOperatorInteraction.Instance;
}
