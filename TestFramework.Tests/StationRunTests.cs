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
/// The seam between the station scope and the runner.
///
/// <see cref="StationResourceTests"/> covers the scopes on their own and
/// <see cref="TestSequenceRunnerTests"/> covers the runner on its own, but the two halves only
/// meet in a host - and a host consumes this repository as packages, so its tests cannot hold this
/// side of the contract up. Everything here is what a host is entitled to assume: a step reaches a
/// station resource without knowing who opened it, the station's hardware outlives any one run,
/// and a run that follows an abandoned step is refused.
/// </summary>
public sealed class StationRunTests
{
    [Fact]
    public async Task RunAsync_StepReadsTheStationInstrumentThroughTheRunScope()
    {
        // A step asks for "psu". Nothing in the sequence opened it - the bench did, before the run
        // started - and the step is not supposed to be able to tell.
        var (resourcePlugins, psuDriver) = Bench();
        var steps = StepRegistry();

        await using var host = new StationResourceHost(Station(Psu()), resourcePlugins);
        var sequence = SequenceReading("psu");

        await using var scope = await host.BeginRunAsync(sequence);
        var result = await new TestSequenceRunner(steps, resources: scope).RunAsync(sequence);

        Assert.Equal(TestVerdict.Pass, result.Verdict);
        Assert.Equal("COM7", Assert.Single(Assert.Single(result.ItemResults).MainResults).Outputs["address"]);
        Assert.Equal(1, psuDriver.Created);
    }

    [Fact]
    public async Task RunAsync_AcrossRuns_KeepsTheStationInstrumentAndReleasesOnlyTheRunScope()
    {
        // One runner for the life of the bench, a fresh scope per run. This is the pattern a host
        // is meant to use: it keeps the runner's quarantine gate spanning run boundaries, which is
        // the point of taking resources per run rather than through the constructor.
        var (resourcePlugins, psuDriver) = Bench();
        var inlineDriver = new CountingInstrumentPlugin("demo.inline");
        resourcePlugins.RegisterInstrumentDriver(inlineDriver);
        var steps = StepRegistry();

        await using var host = new StationResourceHost(Station(Psu()), resourcePlugins);
        var runner = new TestSequenceRunner(steps);

        for (var run = 0; run < 3; run++)
        {
            var sequence = SequenceReading("psu");
            sequence.Instruments =
                [new InstrumentDefinition { Id = "aux", DriverId = "demo.inline", DriverVersion = "1.0.0" }];

            var scope = await host.BeginRunAsync(sequence);
            var result = await runner.RunAsync(sequence, scope);
            await scope.DisposeAsync();

            Assert.Equal(TestVerdict.Pass, result.Verdict);
        }

        // Opened once, never closed: three DUTs, one connection.
        Assert.Equal(1, psuDriver.Created);
        Assert.Equal(0, psuDriver.Instruments[0].DisposeCount);

        // The sequence's own instrument is the opposite: rebuilt and released every run.
        Assert.Equal(3, inlineDriver.Created);
        Assert.All(inlineDriver.Instruments, instrument => Assert.Equal(1, instrument.DisposeCount));
    }

    [Fact]
    public async Task RunAsync_ReusedAcrossRuns_RefusesTheNextRunWhileAPluginHasNotExited()
    {
        // The reason the per-run overload exists. The first run's plugin ignores its timeout and is
        // quarantined; the next DUT must not be started against a plugin instance that is still
        // talking to the hardware. Building a new runner per run - which is what a constructor-only
        // scope forces - would silently lose this and call ExecuteAsync on it a second time.
        var (resourcePlugins, _) = Bench();
        var steps = StepRegistry();
        var blocking = new BlockingStepPlugin();
        steps.Register(blocking);

        await using var host = new StationResourceHost(Station(Psu()), resourcePlugins);
        var runner = new TestSequenceRunner(steps);

        try
        {
            var stuck = SequenceReading("psu");
            stuck.Items[0].MainSteps[0].PluginId = "demo.blocking";
            stuck.Items[0].MainSteps[0].TimeoutMs = 100;

            var firstScope = await host.BeginRunAsync(stuck);
            var first = await runner.RunAsync(stuck, firstScope).WaitAsync(TimeSpan.FromSeconds(3));

            Assert.True(first.HasPendingExecution);
            Assert.False(runner.PendingStepsCompletion.IsCompleted);

            // A second run, with its own fresh scope, is refused by the same runner.
            var nextScope = await host.BeginRunAsync(SequenceReading("psu"));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => runner.RunAsync(SequenceReading("psu"), nextScope));
            await nextScope.DisposeAsync();
        }
        finally
        {
            blocking.Release.TrySetResult();
            await runner.PendingStepsCompletion;
        }
    }

    private static (ResourcePluginRegistry Plugins, CountingInstrumentPlugin PsuDriver) Bench()
    {
        var driver = new CountingInstrumentPlugin();
        var plugins = new ResourcePluginRegistry();
        plugins.RegisterInstrumentDriver(driver);
        return (plugins, driver);
    }

    private static PluginRegistry StepRegistry()
    {
        var steps = new PluginRegistry();
        steps.Register(new InstrumentReadingStepPlugin());
        return steps;
    }

    private static StationConfiguration Station(params StationResourceBinding[] resources) => new()
    {
        StationId = "line-1",
        Name = "工位 1",
        Resources = [.. resources]
    };

    private static StationResourceBinding Psu() => new()
    {
        Alias = "psu",
        Kind = ResourcePluginKind.InstrumentDriver,
        DriverId = "demo.counting",
        DriverVersion = "1.0.0",
        Resource = "COM7"
    };

    private static TestSequence SequenceReading(string alias) => new()
    {
        Name = "Sequence",
        Requires = [new ResourceRequirement { Alias = alias, Kind = ResourcePluginKind.InstrumentDriver }],
        Items =
        [
            new TestItemDefinition
            {
                Name = "Item",
                MainSteps =
                [
                    new TestStepDefinition
                    {
                        Id = "read",
                        Name = "Read",
                        PluginId = "demo.read-instrument",
                        PluginVersion = "1.0.0",
                        Parameters = { ["instrument"] = alias }
                    }
                ],
                VerdictSource = new VerdictSource { StepId = "read", OutputKey = "ok" }
            }
        ]
    };

    private sealed class CountingInstrumentPlugin(string pluginId = "demo.counting") : IInstrumentDriverPlugin
    {
        public ResourcePluginDescriptor Descriptor { get; } = new()
        {
            PluginId = pluginId,
            DisplayName = pluginId,
            Version = new Version(1, 0, 0)
        };

        public Type InstrumentType => typeof(CountingInstrument);

        public int Created { get; private set; }

        public List<CountingInstrument> Instruments { get; } = [];

        public Task<object> CreateAsync(InstrumentDefinition definition, IResourceScope resources, CancellationToken cancellationToken)
        {
            Created++;
            var instrument = new CountingInstrument(definition.Resource);
            Instruments.Add(instrument);
            return Task.FromResult<object>(instrument);
        }
    }

    private sealed class CountingInstrument(string address) : IDisposable
    {
        public string Address { get; } = address;

        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }

    /// <summary>Ignores its cancellation token, which is what gets a step quarantined.</summary>
    private sealed class BlockingStepPlugin : ITestStepPlugin
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TestStepPluginDescriptor Descriptor { get; } = new()
        {
            PluginId = "demo.blocking",
            DisplayName = "Blocking",
            Version = new Version(1, 0, 0)
        };

        public Type SettingsType => typeof(Dictionary<string, object?>);

        public object CreateDefaultSettings() => new Dictionary<string, object?>();

        public object LoadSettings(IReadOnlyDictionary<string, object?> parameters) => parameters;

        public IReadOnlyDictionary<string, object?> SaveSettings(object settings) =>
            (IReadOnlyDictionary<string, object?>)settings;

        public async Task<TestStepResult> ExecuteAsync(
            TestStepExecutionContext context,
            object settings,
            CancellationToken cancellationToken)
        {
            await Release.Task.ConfigureAwait(false);
            return new TestStepResult { Verdict = TestVerdict.Pass };
        }
    }

    private sealed class InstrumentReadingStepPlugin : ITestStepPlugin
    {
        public TestStepPluginDescriptor Descriptor { get; } = new()
        {
            PluginId = "demo.read-instrument",
            DisplayName = "Read instrument",
            Version = new Version(1, 0, 0)
        };

        public Type SettingsType => typeof(Dictionary<string, object?>);

        public object CreateDefaultSettings() => new Dictionary<string, object?>();

        public object LoadSettings(IReadOnlyDictionary<string, object?> parameters) => parameters;

        public IReadOnlyDictionary<string, object?> SaveSettings(object settings) =>
            (IReadOnlyDictionary<string, object?>)settings;

        public Task<TestStepResult> ExecuteAsync(
            TestStepExecutionContext context,
            object settings,
            CancellationToken cancellationToken)
        {
            var parameters = (IReadOnlyDictionary<string, object?>)settings;
            var alias = Convert.ToString(parameters["instrument"])!;
            var instrument = context.Instruments.GetRequired<CountingInstrument>(alias);

            return Task.FromResult(new TestStepResult
            {
                Verdict = TestVerdict.Pass,
                Outputs = { ["address"] = instrument.Address, ["ok"] = true }
            });
        }
    }
}
