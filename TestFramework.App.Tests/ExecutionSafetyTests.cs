using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Plugins;
using TestFramework.Abstractions.Resources;
using TestFramework.App.Services;
using TestFramework.Core.Plugins;
using TestFramework.Core.Resources;
using Xunit;

namespace TestFramework.App.Tests;

public sealed class ExecutionSafetyTests
{
    [Fact]
    public async Task Timeout_KeepsResourceAliveAndBlocksReuseUntilPluginExits()
    {
        var blocking = new BlockingPlugin();
        var plugins = new PluginRegistry();
        plugins.Register(blocking);
        var resource = new TrackedResource();
        var resources = new ResourcePluginRegistry();
        resources.RegisterInstrumentDriver(new InstrumentPlugin(resource));
        var service = new SequenceRunService(plugins, resources);
        var sequence = Sequence();
        try
        {
            var result = await service.RunAsync(sequence, new Observer()).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(TestVerdict.Error, result.Verdict);
            Assert.True(result.HasPendingExecution);
            Assert.False(resource.Disposed);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(sequence, new Observer()));
        }
        finally
        {
            blocking.Release.TrySetResult();
            await service.PendingRecovery.WaitAsync(TimeSpan.FromSeconds(3));
        }
        Assert.True(resource.Disposed);
        Assert.Equal(TestVerdict.Pass, (await service.RunAsync(sequence, new Observer())).Verdict);
    }

    [Fact]
    public async Task CleanupFailure_PreservesCompletedResultsAndBlocksFurtherRuns()
    {
        var blocking = new BlockingPlugin();
        blocking.Release.TrySetResult();
        var plugins = new PluginRegistry();
        plugins.Register(blocking);
        var resources = new ResourcePluginRegistry();
        resources.RegisterInstrumentDriver(new InstrumentPlugin(new TrackedResource { FailOnDispose = true }));
        var service = new SequenceRunService(plugins, resources);
        var result = await service.RunAsync(Sequence(), new Observer());
        Assert.Equal(TestVerdict.Error, result.Verdict);
        Assert.Equal(TestVerdict.Pass, Assert.Single(Assert.Single(result.ItemResults).MainResults).Verdict);
        Assert.Single(result.ResourceErrors);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(Sequence(), new Observer()));
    }

    private static TestSequence Sequence() => new()
    {
        Instruments = [new InstrumentDefinition { Id = "meter", DriverId = "instrument" }],
        Items = [new TestItemDefinition
        {
            MainSteps = [new TestStepDefinition { Id = "main", PluginId = "blocking", TimeoutMs = 100 }],
            VerdictSource = new VerdictSource { StepId = "main" }
        }]
    };

    private sealed class BlockingPlugin : ITestStepPlugin
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TestStepPluginDescriptor Descriptor { get; } = new() { PluginId = "blocking", DisplayName = "Blocking", Version = new(1, 0, 0) };
        public Type SettingsType => typeof(object);
        public object CreateDefaultSettings() => new();
        public object LoadSettings(IReadOnlyDictionary<string, object?> parameters) => new();
        public IReadOnlyDictionary<string, object?> SaveSettings(object settings) => new Dictionary<string, object?>();
        public async Task<TestStepResult> ExecuteAsync(TestStepExecutionContext context, object settings, CancellationToken cancellationToken)
        {
            await Release.Task;
            return new TestStepResult { Verdict = TestVerdict.Pass };
        }
    }

    private sealed class TrackedResource : IDisposable
    {
        public bool Disposed { get; private set; }
        public bool FailOnDispose { get; init; }
        public void Dispose()
        {
            Disposed = true;
            if (FailOnDispose) throw new InvalidOperationException("Instrument cleanup failed.");
        }
    }

    private sealed class InstrumentPlugin(TrackedResource resource) : IInstrumentDriverPlugin
    {
        public ResourcePluginDescriptor Descriptor { get; } = new() { PluginId = "instrument", DisplayName = "Instrument", Version = new(1, 0, 0) };
        public Type InstrumentType => typeof(TrackedResource);
        public Task<object> CreateAsync(InstrumentDefinition definition, RuntimeResourceProvider resources, CancellationToken cancellationToken)
            => Task.FromResult<object>(resource);
    }

    private sealed class Observer : ITestExecutionObserver
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
