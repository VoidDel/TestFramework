using TestFramework.Abstractions.Models;
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

    // Each plugin's declared units join the unit table before the plugin joins its index, so one
    // that contradicts a unit already defined is refused whole - see PluginRegistry.Register.
    public void RegisterInstrumentDriver(IInstrumentDriverPlugin plugin) =>
        Register(plugin, plugin?.Descriptor, _instrumentDrivers);

    public void RegisterTransport(ITransportPlugin plugin) => Register(plugin, plugin?.Descriptor, _transports);

    public void RegisterService(ITestServicePlugin plugin) => Register(plugin, plugin?.Descriptor, _services);

    private static void Register<TPlugin>(TPlugin plugin, ResourcePluginDescriptor? descriptor, VersionedPluginIndex<TPlugin> index)
        where TPlugin : class
    {
        ArgumentNullException.ThrowIfNull(plugin);
        MeasurementUnits.Register(descriptor?.Units ?? []);
        index.Register(plugin);
    }

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

    public Type? GetResourceType(ResourcePluginKind kind, string pluginId, string? version) => kind switch
    {
        ResourcePluginKind.InstrumentDriver => _instrumentDrivers.TryResolve(pluginId, version, out var instrument) ? instrument.Plugin.InstrumentType : null,
        ResourcePluginKind.Transport => _transports.TryResolve(pluginId, version, out var transport) ? transport.Plugin.TransportType : null,
        ResourcePluginKind.Service => _services.TryResolve(pluginId, version, out var service) ? service.Plugin.ServiceType : null,
        _ => null
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
