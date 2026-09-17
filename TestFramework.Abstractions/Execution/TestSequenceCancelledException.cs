namespace TestFramework.Abstractions.Execution;

/// <summary>Preserves the completed portion of a run while retaining cancellation semantics.</summary>
public sealed class TestSequenceCancelledException : OperationCanceledException
{
    public TestSequenceCancelledException(TestSequenceRunResult result, CancellationToken cancellationToken)
        : base("Test sequence was cancelled.", cancellationToken)
    {
        Result = result;
    }

    public TestSequenceRunResult Result { get; }
}
