using TestFramework.Abstractions.Resources;

namespace TestFramework.Core.Resources;

public sealed class ResourcePluginRegistry
{
    private readonly Registry<IInstrumentDriverPlugin> _instrumentDrivers = new(plugin => plugin.Descriptor);
    private readonly Registry<ITransportPlugin> _transports = new(plugin => plugin.Descriptor);
    private readonly Registry<ITestServicePlugin> _services = new(plugin => plugin.Descriptor);

    public IReadOnlyCollection<IInstrumentDriverPlugin> InstrumentDrivers => _instrumentDrivers.Plugins;

    public IReadOnlyCollection<ITransportPlugin> Transports => _transports.Plugins;

    public IReadOnlyCollection<ITestServicePlugin> Services => _services.Plugins;

    public void RegisterInstrumentDriver(IInstrumentDriverPlugin plugin)
    {
        _instrumentDrivers.Register(plugin);
    }

    public void RegisterTransport(ITransportPlugin plugin)
    {
        _transports.Register(plugin);
    }

    public void RegisterService(ITestServicePlugin plugin)
    {
        _services.Register(plugin);
    }

    public IInstrumentDriverPlugin GetRequiredInstrumentDriver(string pluginId, string? version = null)
    {
        return _instrumentDrivers.GetRequired(pluginId, version);
    }

    public ITransportPlugin GetRequiredTransport(string pluginId, string? version = null)
    {
        return _transports.GetRequired(pluginId, version);
    }

    public ITestServicePlugin GetRequiredService(string pluginId, string? version = null)
    {
        return _services.GetRequired(pluginId, version);
    }

    private sealed class Registry<TPlugin>
    {
        private readonly Func<TPlugin, ResourcePluginDescriptor> _getDescriptor;
        private readonly Dictionary<string, List<TPlugin>> _plugins = new(StringComparer.OrdinalIgnoreCase);

        public Registry(Func<TPlugin, ResourcePluginDescriptor> getDescriptor)
        {
            _getDescriptor = getDescriptor;
        }

        public IReadOnlyCollection<TPlugin> Plugins => _plugins.Values
            .SelectMany(plugins => plugins)
            .OrderBy(plugin => _getDescriptor(plugin).Category)
            .ThenBy(plugin => _getDescriptor(plugin).DisplayName)
            .ThenByDescending(plugin => _getDescriptor(plugin).Version)
            .ToArray();

        public void Register(TPlugin plugin)
        {
            ArgumentNullException.ThrowIfNull(plugin);
            var descriptor = _getDescriptor(plugin);
            var pluginId = descriptor.PluginId;

            if (!_plugins.TryGetValue(pluginId, out var versions))
            {
                versions = [];
                _plugins[pluginId] = versions;
            }

            if (versions.Any(existing => _getDescriptor(existing).Version == descriptor.Version))
            {
                throw new InvalidOperationException(
                    $"Resource plugin '{pluginId}' version '{descriptor.Version}' is already registered.");
            }

            versions.Add(plugin);
        }

        public TPlugin GetRequired(string pluginId, string? version)
        {
            if (TryGet(pluginId, version, out var plugin))
            {
                return plugin;
            }

            var versionText = string.IsNullOrWhiteSpace(version) ? string.Empty : $" version '{version}'";
            throw new InvalidOperationException($"Resource plugin '{pluginId}'{versionText} is not registered.");
        }

        private bool TryGet(string pluginId, string? version, out TPlugin plugin)
        {
            plugin = default!;
            if (!_plugins.TryGetValue(pluginId, out var versions) || versions.Count == 0)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(version))
            {
                plugin = versions.OrderByDescending(candidate => _getDescriptor(candidate).Version).First();
                return true;
            }

            if (!Version.TryParse(version, out var parsedVersion))
            {
                return false;
            }

            plugin = versions.FirstOrDefault(candidate => _getDescriptor(candidate).Version == parsedVersion)!;
            return plugin is not null;
        }
    }
}
