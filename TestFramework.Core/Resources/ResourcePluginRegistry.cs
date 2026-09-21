using TestFramework.Abstractions.Plugins;
using TestFramework.Abstractions.Resources;
using TestFramework.Core.Plugins;

namespace TestFramework.Core.Resources;

public sealed class ResourcePluginRegistry : IResourcePluginCatalog
{
    private readonly VersionedPluginIndex<IInstrumentDriverPlugin> _instrumentDrivers =
        new("Instrument driver plugin", plugin => plugin.Descriptor.PluginId, plugin => plugin.Descriptor.Version);

    private readonly VersionedPluginIndex<ITransportPlugin> _transports =
        new("Transport plugin", plugin => plugin.Descriptor.PluginId, plugin => plugin.Descriptor.Version);

    private readonly VersionedPluginIndex<ITestServicePlugin> _services =
        new("Service plugin", plugin => plugin.Descriptor.PluginId, plugin => plugin.Descriptor.Version);

    public IReadOnlyCollection<IInstrumentDriverPlugin> InstrumentDrivers => Sort(_instrumentDrivers.Plugins, plugin => plugin.Descriptor);

    public IReadOnlyCollection<ITransportPlugin> Transports => Sort(_transports.Plugins, plugin => plugin.Descriptor);

    public IReadOnlyCollection<ITestServicePlugin> Services => Sort(_services.Plugins, plugin => plugin.Descriptor);

    public void RegisterInstrumentDriver(IInstrumentDriverPlugin plugin) => _instrumentDrivers.Register(plugin);

    public void RegisterTransport(ITransportPlugin plugin) => _transports.Register(plugin);

    public void RegisterService(ITestServicePlugin plugin) => _services.Register(plugin);

    public bool TryResolve(
        ResourcePluginKind kind,
        string pluginId,
        string? version,
        out PluginVersionMatch match,
        out Version resolvedVersion)
    {
        switch (kind)
        {
            case ResourcePluginKind.InstrumentDriver:
                return Describe(_instrumentDrivers.TryResolve(pluginId, version, out var instrument), instrument.Match, instrument.ResolvedVersion, out match, out resolvedVersion);
            case ResourcePluginKind.Transport:
                return Describe(_transports.TryResolve(pluginId, version, out var transport), transport.Match, transport.ResolvedVersion, out match, out resolvedVersion);
            case ResourcePluginKind.Service:
                return Describe(_services.TryResolve(pluginId, version, out var service), service.Match, service.ResolvedVersion, out match, out resolvedVersion);
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown resource plugin kind.");
        }
    }

    public string DescribeMissing(ResourcePluginKind kind, string pluginId, string? version) => kind switch
    {
        ResourcePluginKind.InstrumentDriver => _instrumentDrivers.DescribeMissing(pluginId, version),
        ResourcePluginKind.Transport => _transports.DescribeMissing(pluginId, version),
        ResourcePluginKind.Service => _services.DescribeMissing(pluginId, version),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown resource plugin kind.")
    };

    private static bool Describe(
        bool resolved,
        PluginVersionMatch sourceMatch,
        Version? sourceVersion,
        out PluginVersionMatch match,
        out Version resolvedVersion)
    {
        match = resolved ? sourceMatch : default;
        resolvedVersion = resolved ? sourceVersion! : new Version(0, 0);
        return resolved;
    }

    public IInstrumentDriverPlugin GetRequiredInstrumentDriver(string pluginId, string? version = null) =>
        _instrumentDrivers.GetRequired(pluginId, version);

    public ITransportPlugin GetRequiredTransport(string pluginId, string? version = null) =>
        _transports.GetRequired(pluginId, version);

    public ITestServicePlugin GetRequiredService(string pluginId, string? version = null) =>
        _services.GetRequired(pluginId, version);

    private static IReadOnlyCollection<TPlugin> Sort<TPlugin>(
        IReadOnlyCollection<TPlugin> plugins,
        Func<TPlugin, ResourcePluginDescriptor> getDescriptor)
    {
        return plugins
            .OrderBy(plugin => getDescriptor(plugin).Category ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(plugin => getDescriptor(plugin).DisplayName)
            .ThenByDescending(plugin => getDescriptor(plugin).Version)
            .ToArray();
    }
}
