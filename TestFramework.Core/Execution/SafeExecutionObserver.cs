using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Models;

namespace TestFramework.Core.Execution;

/// <summary>
/// Isolates observer callbacks from the run. Observers are host code (the desktop UI, a logger,
/// a plugin-supplied listener) and are notified from the runner's happy path as well as from
/// finally blocks. An exception escaping a callback would otherwise replace the in-flight
/// cancellation or completion, discarding the whole run result, so failures are contained here.
/// </summary>
internal sealed class SafeExecutionObserver : ITestExecutionObserver
{
    private readonly ITestExecutionObserver _inner;

    public SafeExecutionObserver(ITestExecutionObserver inner)
    {
        _inner = inner;
    }

    public void SequenceStarted(TestSequence sequence) => Guard(() => _inner.SequenceStarted(sequence));

    public void SequenceFinished(TestSequence sequence, TestSequenceRunResult result) => Guard(() => _inner.SequenceFinished(sequence, result));

    public void ItemStarted(TestItemDefinition item) => Guard(() => _inner.ItemStarted(item));

    public void ItemFinished(TestItemDefinition item, TestItemRunResult result) => Guard(() => _inner.ItemFinished(item, result));

    public void StepStarted(TestItemDefinition item, TestStepDefinition step, StepSection section) => Guard(() => _inner.StepStarted(item, step, section));

    public void StepFinished(TestItemDefinition item, TestStepDefinition step, StepSection section, TestStepResult result) => Guard(() => _inner.StepFinished(item, step, section, result));

    public void Log(string message) => Guard(() => _inner.Log(message));

    private static void Guard(Action callback)
    {
        try
        {
            callback();
        }
        catch
        {
            // A broken observer must not fail the run, and reporting the failure would have to go
            // through the same observer, so it is dropped deliberately.
        }
    }
}
