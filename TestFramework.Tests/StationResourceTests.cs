using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Resources;
using TestFramework.Core.Resources;
using TestFramework.SequenceYaml;
using Xunit;

namespace TestFramework.Tests;

/// <summary>
/// Covers the station scope: what stays open between runs, what is rebuilt, and how a sequence's
/// requirements bind to a bench.
/// </summary>
public sealed class StationResourceTests
{
    private static StationConfiguration Station(params StationResourceBinding[] resources) => new()
    {
        StationId = "line-1",
        Name = "工位 1",
        Resources = [.. resources]
    };

    private static StationResourceBinding Psu(bool shared = true) => new()
    {
        Alias = "psu",
        Kind = ResourcePluginKind.InstrumentDriver,
        DriverId = "demo.counting",
        DriverVersion = "1.0.0",
        Resource = "COM7",
        Shared = shared
    };

    private static (ResourcePluginRegistry Registry, CountingInstrumentPlugin Plugin) Registry()
    {
        var plugin = new CountingInstrumentPlugin();
        var registry = new ResourcePluginRegistry();
        registry.RegisterInstrumentDriver(plugin);
        return (registry, plugin);
    }

    [Fact]
    public async Task SharedResource_IsOpenedOnceAndSurvivesEveryRun()
    {
        // The reason the station scope exists. Opening a USB-CAN adapter per DUT costs about a
        // second each time and some drivers fail outright when cycled.
        var (registry, plugin) = Registry();
        await using var host = new StationResourceHost(Station(Psu()), registry);
        await host.OpenAsync();

        Assert.Equal(1, plugin.Created);

        for (var run = 0; run < 3; run++)
        {
            await using var scope = await host.BeginRunAsync(new TestSequence());
            Assert.True(((IInstrumentProvider)scope).TryGet<CountingInstrument>("psu", out var psu));
            Assert.False(psu.IsDisposed);
        }

        Assert.Equal(1, plugin.Created);
        Assert.Equal(0, plugin.Instruments[0].DisposeCount);
    }

    [Fact]
    public async Task RunScope_ReleasesOnlyWhatTheRunOpened()
    {
        var (registry, plugin) = Registry();
        await using var host = new StationResourceHost(Station(Psu()), registry);

        var sequence = new TestSequence
        {
            Instruments = [new InstrumentDefinition { Id = "dut", DriverId = "demo.counting", DriverVersion = "1.0.0" }]
        };

        var scope = await host.BeginRunAsync(sequence);
        Assert.Equal(2, plugin.Created);
        await scope.DisposeAsync();

        // The sequence's own instrument is gone; the station's is untouched and still reachable.
        var station = plugin.Instruments.Single(instrument => instrument.Address == "COM7");
        var perRun = plugin.Instruments.Single(instrument => instrument.Address != "COM7");
        Assert.True(perRun.IsDisposed);
        Assert.False(station.IsDisposed);

        await using var next = await host.BeginRunAsync(new TestSequence());
        Assert.True(((IInstrumentProvider)next).TryGet<CountingInstrument>("psu", out _));
    }

    [Fact]
    public async Task UnsharedBinding_IsRebuiltForEveryRun()
    {
        // The escape hatch for a resource that genuinely cannot be reused across DUTs.
        var (registry, plugin) = Registry();
        await using var host = new StationResourceHost(Station(Psu(shared: false)), registry);
        await host.OpenAsync();
        Assert.Equal(0, plugin.Created);

        var sequence = new TestSequence
        {
            Requires = [new ResourceRequirement { Alias = "psu", Kind = ResourcePluginKind.InstrumentDriver }]
        };

        var first = await host.BeginRunAsync(sequence);
        Assert.Equal(1, plugin.Created);
        await first.DisposeAsync();
        Assert.True(plugin.Instruments[0].IsDisposed);

        await using var second = await host.BeginRunAsync(sequence);
        Assert.Equal(2, plugin.Created);
    }

    [Fact]
    public async Task ResourceError_MarksTheStationStaleSoTheNextRunReopensIt()
    {
        // A failed limit check says nothing about the CAN channel, but a resource error says the
        // driver may be in any state at all - and the next DUT would inherit it.
        var (registry, plugin) = Registry();
        await using var host = new StationResourceHost(Station(Psu()), registry);
        await host.OpenAsync();
        Assert.Equal(1, plugin.Created);

        var beforeFault = plugin.Instruments[0];
        host.Invalidate();
        Assert.True(host.IsStale);

        // Nothing is torn down at the moment of the fault - the operator may still be looking at it.
        Assert.False(beforeFault.IsDisposed);

        await using var scope = await host.BeginRunAsync(new TestSequence());
        Assert.Equal(2, plugin.Created);
        Assert.True(beforeFault.IsDisposed);
        Assert.False(host.IsStale);
    }

    [Fact]
    public async Task SequenceResource_ShadowsTheStationBindingOfTheSameAlias()
    {
        var (registry, plugin) = Registry();
        await using var host = new StationResourceHost(Station(Psu()), registry);

        var sequence = new TestSequence
        {
            Instruments = [new InstrumentDefinition { Id = "psu", DriverId = "demo.counting", DriverVersion = "1.0.0", Resource = "COM9" }]
        };

        await using var scope = await host.BeginRunAsync(sequence);

        Assert.True(((IInstrumentProvider)scope).TryGet<CountingInstrument>("psu", out var resolved));
        Assert.Equal("COM9", resolved.Address);
        Assert.Equal(2, plugin.Created);
    }

    [Fact]
    public void Check_ReportsAnAliasTheStationDoesNotBind()
    {
        var problems = StationBinding.Check(
            [new ResourceRequirement { Alias = "psu", Description = "程控电源" }],
            Station());

        var problem = Assert.Single(problems);
        Assert.Equal("psu", problem.Alias);
        Assert.Contains("程控电源", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_ReportsAStationThatBindsTheWrongKind()
    {
        var problems = StationBinding.Check(
            [new ResourceRequirement { Alias = "psu", Kind = ResourcePluginKind.InstrumentDriver }],
            Station(new StationResourceBinding { Alias = "psu", Kind = ResourcePluginKind.Transport, DriverId = "demo.transport" }));

        Assert.Contains(problems, problem => problem.Message.Contains("instrument", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_ReportsADriverOlderThanTheSequenceNeeds()
    {
        var requirement = new ResourceRequirement { Alias = "psu", MinimumDriverVersion = "2.0.0" };

        Assert.Single(StationBinding.Check([requirement], Station(Psu())));

        // The same station with a new enough driver satisfies it.
        var upgraded = Psu();
        upgraded.DriverVersion = "2.1.0";
        Assert.Empty(StationBinding.Check([requirement], Station(upgraded)));
    }

    [Fact]
    public void Check_ReportsADriverTheSequenceDidNotAskFor()
    {
        // Naming a driver means asking for that driver's behaviour, not just for something of the
        // same kind, so substitution would be wrong here.
        var problems = StationBinding.Check(
            [new ResourceRequirement { Alias = "psu", DriverId = "keysight.n6700" }],
            Station(Psu()));

        Assert.Contains(problems, problem => problem.Message.Contains("keysight.n6700", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_SequenceWithNoRequirements_NeedsNoStation()
    {
        // Every sequence written before requirements existed lands here.
        Assert.Empty(StationBinding.Check([], null));
    }

    private sealed class CountingInstrumentPlugin : IInstrumentDriverPlugin
    {
        public ResourcePluginDescriptor Descriptor { get; } = new()
        {
            PluginId = "demo.counting",
            DisplayName = "demo.counting",
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

    private sealed class CountingInstrument : IDisposable
    {
        public CountingInstrument(string address) => Address = address;

        public string Address { get; }

        public int DisposeCount { get; private set; }

        public bool IsDisposed => DisposeCount > 0;

        public void Dispose() => DisposeCount++;
    }
}

/// <summary>Covers the station configuration file and the sequence's requirements block.</summary>
public sealed class StationConfigurationYamlTests
{
    [Fact]
    public void Station_RoundTripsThroughYaml()
    {
        var station = new StationConfiguration
        {
            StationId = "line-1",
            Name = "工位 1",
            Resources =
            [
                new StationResourceBinding
                {
                    Alias = "psu",
                    Kind = ResourcePluginKind.InstrumentDriver,
                    DriverId = "keysight.n6700",
                    DriverVersion = "1.2.0",
                    Resource = "COM7",
                    Settings = { ["baudRate"] = 115200 }
                },
                new StationResourceBinding
                {
                    Alias = "can",
                    Kind = ResourcePluginKind.Transport,
                    DriverId = "peak.pcan",
                    Resource = "can0",
                    Channel = "psu",
                    Shared = false
                }
            ]
        };

        var service = new StationConfigurationYamlService();
        var reloaded = service.Load(service.Save(station));

        Assert.Equal("line-1", reloaded.StationId);
        var psu = reloaded.Find("PSU");
        Assert.NotNull(psu);
        Assert.Equal("COM7", psu.Resource);
        // The baud rate has to come back a number, or the driver receives text.
        Assert.Equal(115200, psu.Settings["baudRate"]);
        Assert.True(psu.Shared);

        var can = reloaded.Find("can");
        Assert.NotNull(can);
        Assert.Equal(ResourcePluginKind.Transport, can.Kind);
        Assert.Equal("psu", can.Channel);
        Assert.False(can.Shared);
    }

    [Fact]
    public void SequenceRequirements_RoundTripThroughYaml()
    {
        var sequence = new TestSequence
        {
            Name = "Sequence",
            Requires =
            [
                new ResourceRequirement
                {
                    Alias = "psu",
                    Kind = ResourcePluginKind.InstrumentDriver,
                    MinimumDriverVersion = "1.2.0",
                    Description = "程控电源"
                }
            ]
        };

        var service = new TestSequenceYamlService();
        var reloaded = service.Load(service.Save(sequence));

        var requirement = Assert.Single(reloaded.Requires);
        Assert.Equal("psu", requirement.Alias);
        Assert.Equal("1.2.0", requirement.MinimumDriverVersion);
        Assert.Equal("程控电源", requirement.Description);
    }

    [Fact]
    public void Station_WithAnUnsupportedSchemaVersion_IsRefused()
    {
        var service = new StationConfigurationYamlService();

        Assert.Throws<InvalidDataException>(() => service.Load("schemaVersion: 2\nstationId: line-1\n"));
    }
}
