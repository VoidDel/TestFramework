using Avalonia.Threading;
using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Models;

namespace TestFramework.App.Services;

internal sealed class UiExecutionObserver : ITestExecutionObserver
{
    private readonly Action<string> _appendLog;

    public UiExecutionObserver(Action<string> appendLog)
    {
        _appendLog = appendLog;
    }

    public void SequenceStarted(TestSequence sequence)
    {
        Post($"序列开始：{sequence.Name}");
    }

    public void SequenceFinished(TestSequence sequence, TestSequenceRunResult result)
    {
        Post($"序列完成：{DisplayFormatters.FormatVerdict(result.Verdict)}（{result.Duration.TotalSeconds:F2}s）");
    }

    public void ItemStarted(TestItemDefinition item)
    {
        Post($"测试项开始：{item.Name}");
    }

    public void ItemFinished(TestItemDefinition item, TestItemRunResult result)
    {
        Post($"测试项完成：{item.Name} => {DisplayFormatters.FormatVerdict(result.Verdict)}");
    }

    public void StepStarted(TestItemDefinition item, TestStepDefinition step, StepSection section)
    {
        Post($"  {DisplayFormatters.FormatSection(section)}：{step.Name} 开始");
    }

    public void StepFinished(TestItemDefinition item, TestStepDefinition step, StepSection section, TestStepResult result)
    {
        var suffix = string.IsNullOrWhiteSpace(result.ErrorMessage) ? string.Empty : $" - {result.ErrorMessage}";
        Post($"  {DisplayFormatters.FormatSection(section)}：{step.Name} => {DisplayFormatters.FormatVerdict(result.Verdict)}{suffix}");
    }

    public void Log(string message)
    {
        Post(message);
    }

    private void Post(string message)
    {
        Dispatcher.UIThread.Post(() => _appendLog(message));
    }

}
