using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Plugins;
using TestFramework.Abstractions.Resources;
using TestFramework.Core.Resources;
using Xunit;

namespace TestFramework.Tests;

/// <summary>
/// What an operator is told when a scope fails to open.
///
/// A scope that failed part-way through is still holding everything it managed to open, so it has
/// to be released - and the bench whose meter is missing is exactly the bench whose supply is also
/// in a bad way, so that release is itself likely to fail. Both failures have to survive, with the
/// build error named first: it is the one that says why nothing opened.
/// </summary>
public sealed class StationScopeFailureTests
{
    [Fact]
    public async Task OpenAsync_BindingFailsWhileAnotherRefusesToClose_StillNamesTheRootCause()
    {
        var plugins = new ResourcePluginRegistry();
        plugins.RegisterInstrumentDriver(new StubbornInstrumentPlugin());
        plugins.RegisterInstrumentDriver(new AbsentInstrumentPlugin());

        await using var host = new StationResourceHost(
            Station(
                Binding("psu", "demo.stubborn"),
                Binding("dmm", "demo.absent")),
            plugins);

        var error = await Assert.ThrowsAsync<AggregateException>(() => host.OpenAsync());

        Assert.Equal(AbsentInstrumentPlugin.Message, error.InnerExceptions[0].Message);
        Assert.Contains(error.InnerExceptions, inner => inner.Message.Contains(StubbornInstrument.Message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task BeginRunAsync_ResourceFailsWhileAnotherRefusesToClose_StillNamesTheRootCause()
    {
        // The same rule on the run scope, which opens the sequence's own inline resources.
        var plugins = new ResourcePluginRegistry();
        plugins.RegisterInstrumentDriver(new StubbornInstrumentPlugin());
        plugins.RegisterInstrumentDriver(new AbsentInstrumentPlugin());

        await using var host = new StationResourceHost(Station(), plugins);
        var sequence = new TestSequence
        {
            Name = "Sequence",
            Instruments =
            [
                new InstrumentDefinition { Id = "aux", DriverId = "demo.stubborn", DriverVersion = "1.0.0" },
                new InstrumentDefinition { Id = "dmm", DriverId = "demo.absent", DriverVersion = "1.0.0" }
            ]
        };

        var error = await Assert.ThrowsAsync<AggregateException>(() => host.BeginRunAsync(sequence));

        Assert.Equal(AbsentInstrumentPlugin.Message, error.InnerExceptions[0].Message);
        Assert.Contains(error.InnerExceptions, inner => inner.Message.Contains(StubbornInstrument.Message, StringComparison.Ordinal));
    }

    private static StationConfiguration Station(params StationResourceBinding[] resources) => new()
    {
        StationId = "line-1",
        Name = "工位 1",
        Resources = [.. resources]
    };

    private static StationResourceBinding Binding(string alias, string driverId) => new()
    {
        Alias = alias,
        Kind = ResourcePluginKind.InstrumentDriver,
        DriverId = driverId
    };

    /// <summary>Opens fine and refuses to close: the second failure that must not mask the first.</summary>
    private sealed class StubbornInstrumentPlugin : IInstrumentDriverPlugin
    {
        public ResourcePluginDescriptor Descriptor { get; } = new()
        {
            PluginId = "demo.stubborn",
            DisplayName = "demo.stubborn",
            Version = new Version(1, 0, 0)
        };

        public Type InstrumentType => typeof(StubbornInstrument);

        public Task<object> CreateAsync(InstrumentDefinition definition, IResourceScope resources, CancellationToken cancellationToken) =>
            Task.FromResult<object>(new StubbornInstrument());
    }

    private sealed class StubbornInstrument : IDisposable
    {
        public const string Message = "the supply would not close";

        public void Dispose() => throw new InvalidOperationException(Message);
    }

    /// <summary>Not on the bench: the root cause the operator actually needs to read.</summary>
    private sealed class AbsentInstrumentPlugin : IInstrumentDriverPlugin
    {
        public const string Message = "the meter is not on the bench";

        public ResourcePluginDescriptor Descriptor { get; } = new()
        {
            PluginId = "demo.absent",
            DisplayName = "demo.absent",
            Version = new Version(1, 0, 0)
        };

        public Type InstrumentType => typeof(object);

        public Task<object> CreateAsync(InstrumentDefinition definition, IResourceScope resources, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(Message);
    }
}
