using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Plugins;
using TestFramework.Abstractions.Resources;
using TestFramework.Core.Execution;
using TestFramework.Core.Plugins;
using TestFramework.Core.Resources;
using Xunit;

namespace TestFramework.Tests;

/// <summary>
/// The bench-level rules a host used to re-derive: one runner across runs, a stale station after an
/// Error, no next run while a plugin is still running, and a faulted bench after a failed cleanup.
/// </summary>
public sealed class TestRunSessionTests
{
    [Fact]
    public async Task PassingRuns_ReuseTheStationsInstruments()
    {
        var bench = new Bench();
        await using var station = bench.Station();
        var session = bench.Session(station);

        for (var run = 0; run < 3; run++)
        {
            Assert.Equal(TestVerdict.Pass, (await session.RunAsync(Sequence("pass"))).Verdict);
        }

        Assert.Equal(1, bench.Driver.Created);
    }

    [Theory]
    [InlineData("throw", 2)]
    [InlineData("fail", 1)]
    public async Task OnlyAnError_MakesTheNextRunReopenTheStation(string step, int openedAfterTwoRuns)
    {
        // Error: something threw mid-step, the instrument may be in any state. Fail: a reading came
        // out low, which says nothing about the hardware and must not cost a reconnection.
        var bench = new Bench();
        await using var station = bench.Station();
        var session = bench.Session(station);

        await session.RunAsync(Sequence(step));
        await session.RunAsync(Sequence("pass"));

        Assert.Equal(openedAfterTwoRuns, bench.Driver.Created);
    }

    [Fact]
    public async Task APluginThatHasNotExited_KeepsItsResourcesAndRefusesTheNextRun()
    {
        var bench = new Bench();
        var sequence = Sequence("block");
        sequence.Instruments = [new InstrumentDefinition { Id = "aux", DriverId = "demo.counting", DriverVersion = "1.0.0" }];
        sequence.Items[0].MainSteps[0].TimeoutMs = 100;
        var session = bench.Session(station: null);

        try
        {
            var first = await session.RunAsync(sequence).WaitAsync(TimeSpan.FromSeconds(3));

            Assert.True(first.HasPendingExecution);
            Assert.Equal(0, bench.Driver.Instruments[0].DisposeCount);
            var refused = await Assert.ThrowsAsync<TestRunRefusedException>(() => session.RunAsync(Sequence("pass")));
            Assert.Equal(TestRunRefusal.PluginStillRunning, refused.Reason);
        }
        finally
        {
            bench.Blocking.Release.TrySetResult();
        }

        await session.PendingRecovery.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, bench.Driver.Instruments[0].DisposeCount);
        Assert.Equal(TestVerdict.Pass, (await session.RunAsync(Sequence("pass"))).Verdict);
    }

    [Fact]
    public async Task AFailedCleanup_FaultsTheBenchUntilTheHostClearsIt()
    {
        var bench = new Bench();
        var sequence = Sequence("pass");
        sequence.Instruments = [new InstrumentDefinition { Id = "aux", DriverId = "demo.sticky", DriverVersion = "1.0.0" }];
        var session = bench.Session(station: null);

        var result = await session.RunAsync(sequence);

        Assert.Equal(TestVerdict.Error, result.Verdict);
        Assert.Contains(result.ResourceErrors, error => error.Contains("relay welded", StringComparison.Ordinal));
        Assert.NotNull(session.Fault);
        var refused = await Assert.ThrowsAsync<TestRunRefusedException>(() => session.RunAsync(Sequence("pass")));
        Assert.Equal(TestRunRefusal.Faulted, refused.Reason);

        session.ClearFault();
        Assert.Equal(TestVerdict.Pass, (await session.RunAsync(Sequence("pass"))).Verdict);
    }

    [Fact]
    public async Task ASecondRunWhileOneIsInProgress_IsRefusedNotQueued()
    {
        var bench = new Bench();
        var session = bench.Session(station: null);
        var running = session.RunAsync(Sequence("block"));
        await bench.Blocking.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        try
        {
            var refused = await Assert.ThrowsAsync<TestRunRefusedException>(() => session.RunAsync(Sequence("pass")));
            Assert.Equal(TestRunRefusal.RunInProgress, refused.Reason);
        }
        finally
        {
            bench.Blocking.Release.TrySetResult();
            await running;
        }
    }

    [Fact]
    public async Task Cancellation_StillReturnsTheResult()
    {
        var bench = new Bench();
        var session = bench.Session(station: null);
        using var cancel = new CancellationTokenSource();
        var sequence = Sequence("cancellable");

        var run = session.RunAsync(sequence, cancellationToken: cancel.Token);
        await bench.Cancellable.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancel.Cancel();
        var result = await run;

        Assert.Equal(TestVerdict.Cancelled, result.Verdict);
        Assert.Single(result.ItemResults);
    }

    [Fact]
    public async Task RunInfo_AndTheSessionsOptions_ReachTheRun()
    {
        var bench = new Bench();
        var resolver = new SingleResolver(Sequence("pass"));
        var session = new TestRunSession(bench.Steps, bench.Resources, options: new TestRunSessionOptions { SequenceResolver = resolver });
        var caller = new TestSequence
        {
            Name = "caller",
            Items = [new TestItemDefinition { Id = "call", Name = "call", Call = new SequenceCallDefinition { Path = "any" } }]
        };

        var result = await session.RunAsync(caller, new TestRunInfo { DutSerialNumber = "BMS-7", StationId = "EOL-1" });

        Assert.Equal(TestVerdict.Pass, result.Verdict);
        Assert.Equal("BMS-7", result.RunInfo.DutSerialNumber);
        Assert.Equal("EOL-1", result.RunInfo.StationId);
    }

    [Fact]
    public async Task ResourcesThatCannotOpen_GiveAnErrorResultRatherThanAnException()
    {
        var bench = new Bench();
        var sequence = Sequence("pass");
        sequence.Instruments = [new InstrumentDefinition { Id = "aux", DriverId = "demo.missing", DriverVersion = "1.0.0" }];

        var result = await bench.Session(station: null).RunAsync(sequence);

        Assert.Equal(TestVerdict.Error, result.Verdict);
        Assert.NotEmpty(result.ResourceErrors);
    }

    private static TestSequence Sequence(string stepPlugin) => new()
    {
        Name = "Sequence",
        Items =
        [
            new TestItemDefinition
            {
                Id = "item",
                Name = "Item",
                MainSteps = [new TestStepDefinition { Id = "step", Name = "step", PluginId = "demo." + stepPlugin }],
                VerdictSource = new VerdictSource { StepId = "step" }
            }
        ]
    };

    private sealed class Bench
    {
        public Bench()
        {
            Resources.RegisterInstrumentDriver(Driver);
            Resources.RegisterInstrumentDriver(new StickyInstrumentPlugin());
            Steps.Register(new VerdictPlugin("demo.pass", TestVerdict.Pass));
            Steps.Register(new VerdictPlugin("demo.fail", TestVerdict.Fail));
            Steps.Register(new VerdictPlugin("demo.throw", TestVerdict.Error));
            Steps.Register(Blocking);
            Steps.Register(Cancellable);
        }

        public CountingInstrumentPlugin Driver { get; } = new();

        public ResourcePluginRegistry Resources { get; } = new();

        public PluginRegistry Steps { get; } = new();

        public WaitingPlugin Blocking { get; } = new("demo.block", honoursCancellation: false);

        public WaitingPlugin Cancellable { get; } = new("demo.cancellable", honoursCancellation: true);

        public StationResourceHost Station() => new(
            new StationConfiguration
            {
                StationId = "bench",
                Resources = [new StationResourceBinding { Alias = "psu", DriverId = "demo.counting", DriverVersion = "1.0.0", Resource = "COM7" }]
            },
            Resources);

        public TestRunSession Session(StationResourceHost? station) => new(Steps, Resources, station);
    }

    private sealed class SingleResolver(TestSequence sequence) : ISequenceResolver
    {
        public TestSequence Resolve(string path) => sequence;
    }

    private sealed class CountingInstrumentPlugin : IInstrumentDriverPlugin
    {
        public ResourcePluginDescriptor Descriptor { get; } = new() { PluginId = "demo.counting", DisplayName = "counting", Version = new Version(1, 0, 0) };

        public Type InstrumentType => typeof(CountingInstrument);

        public int Created => Instruments.Count;

        public List<CountingInstrument> Instruments { get; } = [];

        public Task<object> CreateAsync(InstrumentDefinition definition, IResourceScope resources, CancellationToken cancellationToken)
        {
            var instrument = new CountingInstrument();
            Instruments.Add(instrument);
            return Task.FromResult<object>(instrument);
        }
    }

    private sealed class CountingInstrument : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }

    /// <summary>An instrument that will not let go: its cleanup throws.</summary>
    private sealed class StickyInstrumentPlugin : IInstrumentDriverPlugin
    {
        public ResourcePluginDescriptor Descriptor { get; } = new() { PluginId = "demo.sticky", DisplayName = "sticky", Version = new Version(1, 0, 0) };

        public Type InstrumentType => typeof(StickyInstrument);

        public Task<object> CreateAsync(InstrumentDefinition definition, IResourceScope resources, CancellationToken cancellationToken) =>
            Task.FromResult<object>(new StickyInstrument());
    }

    private sealed class StickyInstrument : IDisposable
    {
        public void Dispose() => throw new IOException("relay welded");
    }

    private sealed class VerdictPlugin(string pluginId, TestVerdict verdict) : ITestStepPlugin
    {
        public TestStepPluginDescriptor Descriptor { get; } = new() { PluginId = pluginId, DisplayName = pluginId, Version = new Version(1, 0, 0) };

        public Type SettingsType => typeof(object);

        public object CreateDefaultSettings() => new();

        public object LoadSettings(IReadOnlyDictionary<string, object?> parameters) => new();

        public IReadOnlyDictionary<string, object?> SaveSettings(object settings) => new Dictionary<string, object?>();

        public Task<TestStepResult> ExecuteAsync(TestStepExecutionContext context, object settings, CancellationToken cancellationToken) =>
            verdict == TestVerdict.Error
                ? throw new InvalidOperationException("instrument stopped answering")
                : Task.FromResult(new TestStepResult { Verdict = verdict });
    }

    private sealed class WaitingPlugin(string pluginId, bool honoursCancellation) : ITestStepPlugin
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TestStepPluginDescriptor Descriptor { get; } = new() { PluginId = pluginId, DisplayName = pluginId, Version = new Version(1, 0, 0) };

        public Type SettingsType => typeof(object);

        public object CreateDefaultSettings() => new();

        public object LoadSettings(IReadOnlyDictionary<string, object?> parameters) => new();

        public IReadOnlyDictionary<string, object?> SaveSettings(object settings) => new Dictionary<string, object?>();

        public async Task<TestStepResult> ExecuteAsync(TestStepExecutionContext context, object settings, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(honoursCancellation ? cancellationToken : CancellationToken.None);
            return new TestStepResult { Verdict = TestVerdict.Pass };
        }
    }
}
