using TestFramework.Abstractions.Plugins;

namespace TestFramework.Core.Plugins;

public sealed class PluginRegistry : IPluginRegistry
{
    private readonly Dictionary<string, List<ITestStepPlugin>> _plugins = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<ITestStepPlugin> Plugins => _plugins.Values
        .SelectMany(plugins => plugins)
        .OrderBy(plugin => plugin.Descriptor.Category)
        .ThenBy(plugin => plugin.Descriptor.DisplayName)
        .ThenByDescending(plugin => plugin.Descriptor.Version)
        .ToArray();

    public void Register(ITestStepPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        var pluginId = plugin.Descriptor.PluginId;
        if (!_plugins.TryGetValue(pluginId, out var versions))
        {
            versions = [];
            _plugins[pluginId] = versions;
        }

        if (versions.Any(existing => existing.Descriptor.Version == plugin.Descriptor.Version))
        {
            throw new InvalidOperationException(
                $"Test step plugin '{pluginId}' version '{plugin.Descriptor.Version}' is already registered.");
        }

        versions.Add(plugin);
    }

    public ITestStepPlugin GetRequired(string pluginId, string? version = null)
    {
        if (TryGet(pluginId, version, out var plugin))
        {
            return plugin;
        }

        var versionText = string.IsNullOrWhiteSpace(version) ? string.Empty : $" version '{version}'";
        throw new InvalidOperationException($"Test step plugin '{pluginId}'{versionText} is not registered.");
    }

    public bool TryGet(string pluginId, out ITestStepPlugin plugin)
    {
        return TryGet(pluginId, null, out plugin);
    }

    public bool TryGet(string pluginId, string? version, out ITestStepPlugin plugin)
    {
        plugin = null!;
        if (!_plugins.TryGetValue(pluginId, out var versions) || versions.Count == 0)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            plugin = versions.OrderByDescending(candidate => candidate.Descriptor.Version).First();
            return true;
        }

        if (!Version.TryParse(version, out var parsedVersion))
        {
            return false;
        }

        plugin = versions.FirstOrDefault(candidate => candidate.Descriptor.Version == parsedVersion)!;
        return plugin is not null;
    }
}
