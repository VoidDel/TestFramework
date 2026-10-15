using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Resources;
using TestFramework.Core.Resources;
using TestFramework.Instruments;
using TestFramework.Plugins.Simulation;
using TestFramework.SequenceYaml;
using TestFramework.SequenceYaml.Validation;
using Xunit;

namespace TestFramework.Tests;

/// <summary>
/// A requirement names what a resource is, and the validator, the station and the resource builder
/// each hold the bound driver to it - so a multimeter bound where a supply is meant stops before
/// the run, never at a step's cast.
/// </summary>
public sealed class ResourceClassTests
{
    [Fact]
    public void ResourceClasses_AreTheTypesOwnAndItsInterfaces_ComparedCaseInsensitively()
    {
        Assert.Equal([InstrumentClasses.PowerSupply], ResourceClasses.Of(typeof(SimulatedPowerSupply)));
        Assert.True(ResourceClasses.Implements(typeof(SimulatedPowerSupply), "powersupply"));
        Assert.False(ResourceClasses.Implements(typeof(SimulatedPowerSupply), InstrumentClasses.DigitalMultimeter));
        Assert.Empty(ResourceClasses.Of(typeof(string)));

        // A bidirectional source is both.
        Assert.Equal(2, ResourceClasses.Of(typeof(BidirectionalSource)).Count);
    }

    [Fact]
    public void Validator_RefusesADriverThatIsNotOfTheRequiredClass()
    {
        var station = Station(("psu", "sim.dmm", ResourcePluginKind.InstrumentDriver));

        var issue = Assert.Single(new TestSequenceValidator(resourcePlugins: Drivers(), station: station).Validate(Requiring("psu", InstrumentClasses.PowerSupply)));

        Assert.True(issue.IsError);
        Assert.Equal("requires[0].alias", issue.Path);
        Assert.Contains("PowerSupply", issue.Message, StringComparison.Ordinal);
        Assert.Contains("sim.dmm", issue.Message, StringComparison.Ordinal);
        Assert.Contains("DigitalMultimeter", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validator_AcceptsTheRightClass_AndChecksALineBindingToo()
    {
        var station = Station(("psu", "sim.psu", ResourcePluginKind.InstrumentDriver));
        var line = Station(("dmm", "sim.psu", ResourcePluginKind.InstrumentDriver));
        var sequence = Requiring("psu", InstrumentClasses.PowerSupply);
        sequence.Requires.Add(new ResourceRequirement { Alias = "dmm", Class = InstrumentClasses.DigitalMultimeter });

        var issue = Assert.Single(new TestSequenceValidator(resourcePlugins: Drivers(), station: station, line: line).Validate(sequence));

        Assert.Equal("requires[1].alias", issue.Path);
        Assert.Contains("DigitalMultimeter", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validator_SaysNothingAboutClassesWithoutAClassOrWithoutTheDrivers()
    {
        var station = Station(("psu", "sim.dmm", ResourcePluginKind.InstrumentDriver));

        Assert.Empty(new TestSequenceValidator(resourcePlugins: Drivers(), station: station).Validate(Requiring("psu", @class: null)));
        Assert.Empty(new TestSequenceValidator(station: station).Validate(Requiring("psu", InstrumentClasses.PowerSupply)));
    }

    [Fact]
    public async Task Station_RefusesARunWhoseResourceIsNotOfTheRequiredClass()
    {
        await using var host = new StationResourceHost(Station(("psu", "sim.dmm", ResourcePluginKind.InstrumentDriver)), Drivers());

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => host.BeginRunAsync(Requiring("psu", InstrumentClasses.PowerSupply)));

        Assert.Contains("'psu' must be a PowerSupply", refused.Message, StringComparison.Ordinal);
        Assert.Contains("DigitalMultimeter", refused.Message, StringComparison.Ordinal);

        await using var scope = await host.BeginRunAsync(Requiring("psu", InstrumentClasses.DigitalMultimeter));
        Assert.True(scope.Scope.Instruments.TryGet<IDigitalMultimeter>("psu", out _));
    }

    [Fact]
    public async Task Builder_RefusesAResourceThatIsNotTheTypeItsPluginDeclares()
    {
        var plugins = new ResourcePluginRegistry();
        plugins.RegisterInstrumentDriver(new LyingDriver());
        await using var host = new StationResourceHost(Station(("psu", "probe.liar", ResourcePluginKind.InstrumentDriver)), plugins);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => host.OpenAsync());

        Assert.Contains("declares that it creates", refused.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(IPowerSupply), refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Yaml_RoundTripsTheClass_AndOmitsItWhenAbsent()
    {
        var yaml = new TestSequenceYamlService();
        var withClass = yaml.Save(Requiring("psu", InstrumentClasses.PowerSupply));
        var without = yaml.Save(Requiring("psu", @class: null));

        Assert.Contains("class: PowerSupply", withClass, StringComparison.Ordinal);
        Assert.DoesNotContain("class:", without, StringComparison.Ordinal);
        Assert.Equal(InstrumentClasses.PowerSupply, Assert.Single(yaml.Load(withClass).Requires).Class);
        Assert.Null(Assert.Single(yaml.Load(without).Requires).Class);
    }

    private static TestSequence Requiring(string alias, string? @class) => new()
    {
        Name = "Sequence",
        Requires = [new ResourceRequirement { Alias = alias, Kind = ResourcePluginKind.InstrumentDriver, Class = @class }]
    };

    private static StationConfiguration Station(params (string Alias, string DriverId, ResourcePluginKind Kind)[] bindings) => new()
    {
        StationId = "bench",
        Resources = bindings
            .Select(binding => new StationResourceBinding { Alias = binding.Alias, Kind = binding.Kind, DriverId = binding.DriverId, DriverVersion = "1.0.0" })
            .ToList()
    };

    private static ResourcePluginRegistry Drivers()
    {
        var plugins = new ResourcePluginRegistry();
        plugins.RegisterInstrumentDriver(new SimulatedPowerSupplyDriver());
        plugins.RegisterInstrumentDriver(new SimulatedMultimeterDriver());
        return plugins;
    }

    private abstract class BidirectionalSource : IPowerSupply, IElectronicLoad
    {
        public abstract Task<string> IdentifyAsync(CancellationToken cancellationToken = default);
        public abstract Task ResetAsync(CancellationToken cancellationToken = default);
        public abstract Task SetVoltageAsync(double volts, CancellationToken cancellationToken = default);
        public abstract Task SetCurrentLimitAsync(double amperes, CancellationToken cancellationToken = default);
        public abstract Task SetOverVoltageProtectionAsync(double? volts, CancellationToken cancellationToken = default);
        public abstract Task ClearProtectionAsync(CancellationToken cancellationToken = default);
        public abstract Task SetOutputEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
        public abstract Task<double> MeasureVoltageAsync(CancellationToken cancellationToken = default);
        public abstract Task<double> MeasureCurrentAsync(CancellationToken cancellationToken = default);
        public abstract Task<PowerSupplyStatus> QueryStatusAsync(CancellationToken cancellationToken = default);
        public abstract Task SetModeAsync(LoadMode mode, double level, CancellationToken cancellationToken = default);
        public abstract Task SetInputEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
    }

    /// <summary>Declares a supply and returns something else - the bug the builder's check exists for.</summary>
    private sealed class LyingDriver : IInstrumentDriverPlugin
    {
        public ResourcePluginDescriptor Descriptor { get; } = new() { PluginId = "probe.liar", DisplayName = "liar", Version = new Version(1, 0, 0) };

        public Type InstrumentType => typeof(IPowerSupply);

        public Task<object> CreateAsync(InstrumentDefinition definition, IResourceScope resources, CancellationToken cancellationToken) =>
            Task.FromResult<object>("not a supply");
    }
}
