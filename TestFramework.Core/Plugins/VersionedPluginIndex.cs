using TestFramework.Abstractions.Plugins;

namespace TestFramework.Core.Plugins;

/// <summary>
/// Stores plugins of one kind by id and version, and resolves them through
/// <see cref="PluginVersionPolicy"/>. Step plugins and resource plugins differ only in their
/// contract types, so keeping one index means the version rule has a single implementation and
/// cannot drift between the two.
///
/// Registration is expected to happen once during host start-up. Lookups are taken on background
/// step-execution threads afterwards, so reads are guarded rather than left to chance.
/// </summary>
internal sealed class VersionedPluginIndex<TPlugin>
    where TPlugin : class
{
    private readonly Func<TPlugin, string> _getPluginId;
    private readonly Func<TPlugin, Version> _getVersion;
    private readonly string _kind;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Dictionary<Version, TPlugin>> _plugins = new(StringComparer.OrdinalIgnoreCase);

    public VersionedPluginIndex(string kind, Func<TPlugin, string> getPluginId, Func<TPlugin, Version> getVersion)
    {
        _kind = kind;
        _getPluginId = getPluginId;
        _getVersion = getVersion;
    }

    public IReadOnlyCollection<TPlugin> Plugins
    {
        get
        {
            lock (_gate)
            {
                return _plugins.Values.SelectMany(versions => versions.Values).ToArray();
            }
        }
    }

    public void Register(TPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        var pluginId = _getPluginId(plugin);
        var version = _getVersion(plugin);

        // A blank id or a missing version would make the plugin unaddressable from a sequence file
        // and would collide with every other malformed plugin, so it is rejected at the door.
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            throw new InvalidOperationException($"{_kind} of type '{plugin.GetType().FullName}' declares an empty plugin ID.");
        }

        ArgumentNullException.ThrowIfNull(version);

        lock (_gate)
        {
            if (!_plugins.TryGetValue(pluginId, out var versions))
            {
                versions = [];
                _plugins[pluginId] = versions;
            }

            if (!versions.TryAdd(version, plugin))
            {
                throw new InvalidOperationException($"{_kind} '{pluginId}' version '{version}' is already registered.");
            }
        }
    }

    public bool TryResolve(string pluginId, string? version, out PluginResolution<TPlugin> resolution)
    {
        resolution = default;
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_plugins.TryGetValue(pluginId, out var versions) || versions.Count == 0)
            {
                return false;
            }

            if (!PluginVersionPolicy.TrySelect(versions.Keys, version, out var selected, out var match))
            {
                return false;
            }

            resolution = new PluginResolution<TPlugin>(versions[selected], match, version, selected);
            return true;
        }
    }

    public TPlugin GetRequired(string pluginId, string? version)
    {
        if (TryResolve(pluginId, version, out var resolution))
        {
            return resolution.Plugin;
        }

        throw new InvalidOperationException(DescribeMissing(pluginId, version));
    }

    /// <summary>
    /// Builds the message for an unresolvable reference. It names the installed versions, because
    /// "not registered" alone cannot distinguish a missing plugin from a version mismatch - the two
    /// need completely different fixes.
    /// </summary>
    public string DescribeMissing(string pluginId, string? version)
    {
        lock (_gate)
        {
            if (!_plugins.TryGetValue(pluginId, out var versions) || versions.Count == 0)
            {
                return $"{_kind} '{pluginId}' is not registered.";
            }

            var installed = PluginVersionPolicy.DescribeInstalled(versions.Keys);
            return string.IsNullOrWhiteSpace(version)
                ? $"{_kind} '{pluginId}' is not registered."
                : $"{_kind} '{pluginId}' version '{version}' is not available. Installed versions: {installed}.";
        }
    }
}
