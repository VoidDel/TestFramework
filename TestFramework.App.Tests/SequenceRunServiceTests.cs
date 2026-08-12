using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Plugins;
using TestFramework.App.Services;
using TestFramework.Core.Plugins;
using TestFramework.Core.Resources;
using Xunit;

namespace TestFramework.App.Tests;

public sealed class SequenceRunServiceTests
{
    [Fact]
    public async Task RunAsync_SerializesConcurrentRunsAgainstSharedPluginInstances()
    {
        var plugin = new ConcurrencyDetectingPlugin();
        var registry = new PluginRegistry();
        registry.Register(plugin);
        var service = new SequenceRunService(registry, new ResourcePluginRegistry());
        var sequence = Sequence();

        await Task.WhenAll(
            service.RunAsync(sequence, new NoOpObserver()),
            service.RunAsync(sequence, new NoOpObserver()));

        Assert.Equal(1, plugin.MaximumConcurrency);
    }

    private static TestSequence Sequence()
    {
        var step = new TestStepDefinition
        {
            Id = "main",
            Name = "Main",
            PluginId = "concurrency",
            PluginVersion = "1.0.0"
        };
        return new TestSequence
        {
            Items =
            [
                new TestItemDefinition
                {
                    Name = "Item",
                    MainSteps = [step],
                    VerdictSource = new VerdictSource { StepId = step.Id }
                }
            ]
        };
    }

    private sealed class ConcurrencyDetectingPlugin : ITestStepPlugin
    {
        private int _concurrency;
        private int _maximumConcurrency;
        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);
        public TestStepPluginDescriptor Descriptor { get; } = new()
        {
            PluginId = "concurrency",
            DisplayName = "Concurrency",
            Version = new Version(1, 0, 0)
        };
        public Type SettingsType => typeof(object);
        public object CreateDefaultSettings() => new();
        public object LoadSettings(IReadOnlyDictionary<string, object?> parameters) => new();
        public IReadOnlyDictionary<string, object?> SaveSettings(object settings) => new Dictionary<string, object?>();

        public async Task<TestStepResult> ExecuteAsync(TestStepExecutionContext context, object settings, CancellationToken cancellationToken)
        {
            var concurrency = Interlocked.Increment(ref _concurrency);
            SetMaximum(concurrency);
            try
            {
                await Task.Delay(50, cancellationToken);
                return new TestStepResult { Verdict = TestVerdict.Pass };
            }
            finally
            {
                Interlocked.Decrement(ref _concurrency);
            }
        }

        private void SetMaximum(int value)
        {
            var current = Volatile.Read(ref _maximumConcurrency);
            while (current < value)
            {
                var observed = Interlocked.CompareExchange(ref _maximumConcurrency, value, current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }
    }

    private sealed class NoOpObserver : ITestExecutionObserver
    {
        public void SequenceStarted(TestSequence sequence) { }
        public void SequenceFinished(TestSequence sequence, TestSequenceRunResult result) { }
        public void ItemStarted(TestItemDefinition item) { }
        public void ItemFinished(TestItemDefinition item, TestItemRunResult result) { }
        public void StepStarted(TestItemDefinition item, TestStepDefinition step, StepSection section) { }
        public void StepFinished(TestItemDefinition item, TestStepDefinition step, StepSection section, TestStepResult result) { }
        public void Log(string message) { }
    }
}
