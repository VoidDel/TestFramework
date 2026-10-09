using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Plugins;
using TestFramework.Abstractions.Resources;
using TestFramework.Core.Execution;
using TestFramework.Core.Plugins;
using TestFramework.Core.Resources;
using TestFramework.SequenceYaml;
using TestFramework.SequenceYaml.Validation;
using Xunit;

namespace TestFramework.Tests;

/// <summary>
/// Several stations sharing one instrument: the line scope every station falls through to, the
/// exclusive leases that make them take turns on it, and the rebuild that does not pull the
/// instrument out from under a station still using it.
/// </summary>
public sealed class LineResourceTests
{
    [Fact]
    public async Task StationsOnOneLine_SeeTheSameLineInstrument_AndKeepTheirOwn()
    {
        var bench = new Bench();
        await using var line = bench.Line();
        await using var stationA = bench.Station("A", line);
        await using var stationB = bench.Station("B", line);

        await using var scopeA = await stationA.BeginRunAsync(Sequence());
        await using var scopeB = await stationB.BeginRunAsync(Sequence());

        var dmmA = scopeA.Scope.Instruments.GetRequired<SharedInstrument>("dmm");
        var dmmB = scopeB.Scope.Instruments.GetRequired<SharedInstrument>("dmm");
        Assert.Same(dmmA, dmmB);
        Assert.NotSame(
            scopeA.Scope.Instruments.GetRequired<SharedInstrument>("psu"),
            scopeB.Scope.Instruments.GetRequired<SharedInstrument>("psu"));
        Assert.Equal(3, bench.Driver.Created);
    }

    [Fact]
    public void Requirement_TheLineBinds_PassesTheBindingCheck()
    {
        var requirement = new ResourceRequirement { Alias = "dmm", Kind = ResourcePluginKind.InstrumentDriver };

        Assert.Empty(StationBinding.Check([requirement], Bench.StationConfig("A"), Bench.LineConfig()));
        var problem = Assert.Single(StationBinding.Check([requirement], Bench.StationConfig("A")));
        Assert.Contains("nor its line", problem.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    public async Task ExclusiveStep_TakesTurnsOnTheLineInstrumentAcrossStations(bool exclusive, int expectedPeak)
    {
        var bench = new Bench();
        await using var line = bench.Line();
        await using var stationA = bench.Station("A", line);
        await using var stationB = bench.Station("B", line);
        var sessionA = new TestRunSession(bench.Steps, bench.Resources, stationA);
        var sessionB = new TestRunSession(bench.Steps, bench.Resources, stationB);
        TestSequence Using() => Sequence(UseStep("dmm", holdMs: 150, exclusive: exclusive ? ["dmm"] : []));

        var results = await Task.WhenAll(sessionA.RunAsync(Using()), sessionB.RunAsync(Using()));

        Assert.All(results, result => Assert.Equal(TestVerdict.Pass, result.Verdict));
        Assert.Equal(expectedPeak, bench.Driver.Instruments.Single(instrument => instrument.Alias == "dmm").PeakUsers);
    }

    [Fact]
    public async Task ExclusiveOnAnAliasNothingProvides_IsAStepError()
    {
        var bench = new Bench();
        await using var line = bench.Line();
        await using var station = bench.Station("A", line);
        var session = new TestRunSession(bench.Steps, bench.Resources, station);

        var result = await session.RunAsync(Sequence(UseStep("dmm", holdMs: 0, exclusive: ["scope"])));

        var step = result.ItemResults[0].MainResults[0];
        Assert.Equal(TestVerdict.Error, step.Verdict);
        Assert.Contains("'scope'", step.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TimeoutWhileWaitingForExclusiveUse_SaysWhatItWaitedFor()
    {
        var bench = new Bench();
        await using var line = bench.Line();
        await using var stationA = bench.Station("A", line);
        await using var stationB = bench.Station("B", line);
        var sessionA = new TestRunSession(bench.Steps, bench.Resources, stationA);
        var sessionB = new TestRunSession(bench.Steps, bench.Resources, stationB);
        await line.OpenAsync();
        var dmm = bench.Driver.Instruments.Single(instrument => instrument.Alias == "dmm");

        var holding = sessionA.RunAsync(Sequence(UseStep("dmm", holdMs: 3000, exclusive: ["dmm"])));
        await dmm.FirstUse.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var impatient = UseStep("dmm", holdMs: 0, exclusive: ["dmm"]);
        impatient.TimeoutMs = 500;
        impatient.OnError = ErrorHandlingMode.Continue;
        var result = await sessionB.RunAsync(Sequence(impatient));
        await holding;

        Assert.Contains("exclusive use of 'dmm'", result.ItemResults[0].MainResults[0].ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LineInvalidate_MarksStationsStale_RebuildsOnce_AndRetiresTheOldGenerationOnlyWhenReleased()
    {
        var bench = new Bench();
        await using var line = bench.Line();
        await using var stationA = bench.Station("A", line);
        await using var stationB = bench.Station("B", line);
        await stationA.OpenAsync();
        await stationB.OpenAsync();
        var oldDmm = bench.Driver.Instruments.Single(instrument => instrument.Alias == "dmm");

        line.Invalidate();

        Assert.True(line.IsStale);
        Assert.True(stationA.IsStale);
        Assert.True(stationB.IsStale);

        // The first station back rebuilds the line; the old DMM stays open for the other station.
        await using (await stationA.BeginRunAsync(Sequence()))
        {
            Assert.Equal(2, bench.Driver.Instruments.Count(instrument => instrument.Alias == "dmm"));
            Assert.Equal(0, oldDmm.DisposeCount);
        }

        // The second station moves off the old generation, which is only now released.
        await using (await stationB.BeginRunAsync(Sequence()))
        {
            Assert.Equal(1, oldDmm.DisposeCount);
            Assert.Equal(2, bench.Driver.Instruments.Count(instrument => instrument.Alias == "dmm"));
        }
    }

    [Fact]
    public async Task ResourceCreation_TakesTurnsPerDriverAcrossStations()
    {
        var bench = new Bench(createHoldMs: 100);
        await using var stationA = bench.Station("A", line: null);
        await using var stationB = bench.Station("B", line: null);

        await Task.WhenAll(stationA.OpenAsync(), stationB.OpenAsync());

        Assert.Equal(2, bench.Driver.Created);
        Assert.Equal(1, bench.Driver.PeakCreating);
    }

    [Fact]
    public void Yaml_Exclusive_RoundTrips_AndIsOmittedWhenEmpty()
    {
        var service = new TestSequenceYamlService();
        var sequence = Sequence(UseStep("dmm", holdMs: 0, exclusive: ["dmm", "switch"]));
        sequence.Id = "x";

        var saved = service.Save(sequence);
        var reloaded = service.Load(saved);

        Assert.Equal(["dmm", "switch"], reloaded.Items[0].MainSteps[0].Exclusive);
        Assert.Contains("exclusive:", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("exclusive:", service.Save(Sequence(UseStep("dmm", holdMs: 0, exclusive: []))), StringComparison.Ordinal);
    }

    [Fact]
    public void Validator_AcceptsLineAliases_AndWarnsAboutOnesItCannotSee()
    {
        var sequence = Sequence(UseStep("dmm", holdMs: 0, exclusive: ["dmm"]));
        sequence.Requires.Add(new ResourceRequirement { Alias = "dmm", Kind = ResourcePluginKind.InstrumentDriver });
        var unknown = Sequence(UseStep("dmm", holdMs: 0, exclusive: ["matrix"]));

        var withLine = new TestSequenceValidator(station: Bench.StationConfig("A"), line: Bench.LineConfig()).Validate(sequence);
        var withoutLine = new TestSequenceValidator(station: Bench.StationConfig("A")).Validate(sequence);
        var unknownAlias = new TestSequenceValidator(station: Bench.StationConfig("A"), line: Bench.LineConfig()).Validate(unknown);

        Assert.Empty(withLine);
        Assert.Contains(withoutLine, issue => issue.Path == "requires[0]" || issue.Message.Contains("nor its line"));
        var warning = Assert.Single(unknownAlias);
        Assert.Equal("items[0].main[0].exclusive[0]", warning.Path);
        Assert.Equal(ValidationSeverity.Warning, warning.Severity);
    }

    private static TestSequence Sequence(TestStepDefinition? step = null)
    {
        step ??= UseStep("psu", holdMs: 0, exclusive: []);
        return new TestSequence
        {
            Name = "Sequence",
            Items =
            [
                new TestItemDefinition
                {
                    Id = "item",
                    Name = "Item",
                    MainSteps = [step],
                    VerdictSource = new VerdictSource { StepId = step.Id }
                }
            ]
        };
    }

    private static TestStepDefinition UseStep(string alias, int holdMs, string[] exclusive) => new()
    {
        Id = "use",
        Name = "use",
        PluginId = "demo.use",
        Exclusive = [.. exclusive],
        Parameters = { ["instrument"] = alias, ["holdMs"] = holdMs }
    };

    private sealed class Bench
    {
        public Bench(int createHoldMs = 0)
        {
            Driver = new CountingInstrumentPlugin(createHoldMs);
            Resources.RegisterInstrumentDriver(Driver);
            Steps.Register(new UseInstrumentPlugin());
        }

        public CountingInstrumentPlugin Driver { get; }

        public ResourcePluginRegistry Resources { get; } = new();

        public PluginRegistry Steps { get; } = new();

        public LineResourceHost Line() => new(LineConfig(), Resources);

        public StationResourceHost Station(string id, LineResourceHost? line) => new(StationConfig(id), Resources, line);

        public static StationConfiguration LineConfig() => new()
        {
            StationId = "line-1",
            Name = "产线 1",
            Resources = [Binding("dmm", "GPIB0::22")]
        };

        public static StationConfiguration StationConfig(string id) => new()
        {
            StationId = id,
            Name = $"工位 {id}",
            Resources = [Binding("psu", $"COM-{id}")]
        };

        private static StationResourceBinding Binding(string alias, string resource) => new()
        {
            Alias = alias,
            Kind = ResourcePluginKind.InstrumentDriver,
            DriverId = "demo.counting",
            DriverVersion = "1.0.0",
            Resource = resource
        };
    }

    private sealed class CountingInstrumentPlugin(int createHoldMs) : IInstrumentDriverPlugin
    {
        private int _creating;
        private int _peakCreating;

        public ResourcePluginDescriptor Descriptor { get; } = new() { PluginId = "demo.counting", DisplayName = "counting", Version = new Version(1, 0, 0) };

        public Type InstrumentType => typeof(SharedInstrument);

        public List<SharedInstrument> Instruments { get; } = [];

        public int Created => Instruments.Count;

        public int PeakCreating => Volatile.Read(ref _peakCreating);

        public async Task<object> CreateAsync(InstrumentDefinition definition, IResourceScope resources, CancellationToken cancellationToken)
        {
            Concurrency.Enter(ref _creating, ref _peakCreating);
            if (createHoldMs > 0)
            {
                await Task.Delay(createHoldMs, cancellationToken);
            }

            Interlocked.Decrement(ref _creating);
            var instrument = new SharedInstrument(definition.Id);
            lock (Instruments)
            {
                Instruments.Add(instrument);
            }

            return instrument;
        }
    }

    private sealed class SharedInstrument(string alias) : IDisposable
    {
        private int _users;
        private int _peakUsers;

        public string Alias { get; } = alias;

        public int PeakUsers => Volatile.Read(ref _peakUsers);

        public int DisposeCount { get; private set; }

        public TaskCompletionSource FirstUse { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task UseAsync(int holdMs)
        {
            Concurrency.Enter(ref _users, ref _peakUsers);
            FirstUse.TrySetResult();
            await Task.Delay(holdMs, CancellationToken.None);
            Interlocked.Decrement(ref _users);
        }

        public void Dispose() => DisposeCount++;
    }

    private static class Concurrency
    {
        public static void Enter(ref int inside, ref int peak)
        {
            var now = Interlocked.Increment(ref inside);
            int seen;
            while (now > (seen = Volatile.Read(ref peak)) && Interlocked.CompareExchange(ref peak, now, seen) != seen)
            {
            }
        }
    }

    private sealed class UseInstrumentPlugin : ITestStepPlugin
    {
        public TestStepPluginDescriptor Descriptor { get; } = new() { PluginId = "demo.use", DisplayName = "use", Version = new Version(1, 0, 0) };

        public Type SettingsType => typeof(Dictionary<string, object?>);

        public bool IsThreadSafe => true;

        public object CreateDefaultSettings() => new Dictionary<string, object?>();

        public object LoadSettings(IReadOnlyDictionary<string, object?> parameters) => parameters;

        public IReadOnlyDictionary<string, object?> SaveSettings(object settings) => (IReadOnlyDictionary<string, object?>)settings;

        public async Task<TestStepResult> ExecuteAsync(TestStepExecutionContext context, object settings, CancellationToken cancellationToken)
        {
            var parameters = (IReadOnlyDictionary<string, object?>)settings;
            var instrument = context.Instruments.GetRequired<SharedInstrument>(Convert.ToString(parameters["instrument"])!);
            await instrument.UseAsync(Convert.ToInt32(parameters["holdMs"]));
            return new TestStepResult { Verdict = TestVerdict.Pass };
        }
    }
}
