using TestFramework.Abstractions.Models;

namespace TestFramework.Abstractions.Execution;

public interface ITestExecutionObserver
{
    void SequenceStarted(TestSequence sequence)
    {
    }

    void SequenceFinished(TestSequence sequence, TestSequenceRunResult result)
    {
    }

    void ItemStarted(TestItemDefinition item)
    {
    }

    void ItemFinished(TestItemDefinition item, TestItemRunResult result)
    {
    }

    void StepStarted(TestItemDefinition item, TestStepDefinition step, StepSection section)
    {
    }

    void StepFinished(TestItemDefinition item, TestStepDefinition step, StepSection section, TestStepResult result)
    {
    }

    void Log(string message)
    {
    }
}
