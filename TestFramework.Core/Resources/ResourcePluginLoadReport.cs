using TestFramework.Abstractions.Resources;
using TestFramework.Core.Plugins;

namespace TestFramework.Core.Resources;

public sealed class ResourcePluginLoadReport
{
    public List<IInstrumentDriverPlugin> InstrumentDrivers { get; } = [];

    public List<ITransportPlugin> Transports { get; } = [];

    public List<ITestServicePlugin> Services { get; } = [];

    public List<PluginLoadFailure> Failures { get; } = [];
}
