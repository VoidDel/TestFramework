using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Plugins;

namespace TestFramework.Core.Plugins;

public sealed class PluginRegistry : IPluginRegistry
{
    private readonly VersionedPluginIndex<ITestStepPlugin> _index = new(
        "Test step plugin",
        plugin => plugin.Descriptor.PluginId,
        plugin => plugin.Descriptor.Version);

    public IReadOnlyCollection<ITestStepPlugin> Plugins => _index.Plugins
        .OrderBy(plugin => plugin.Descriptor.Category ?? string.Empty, StringComparer.Ordinal)
        .ThenBy(plugin => plugin.Descriptor.DisplayName)
        .ThenByDescending(plugin => plugin.Descriptor.Version)
        .ToArray();

    /// <summary>
    /// Registers the plugin and the units its descriptor declares. The units go first, all or none,
    /// so a plugin that contradicts a unit already defined is refused whole rather than registered
    /// with outputs the table reads differently from how it meant them. The table only grows: units
    /// of a plugin refused afterwards, as a duplicate, stay - they agreed with the table to get there.
    /// </summary>
    public void Register(ITestStepPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        MeasurementUnits.Register(plugin.Descriptor?.Units ?? []);
        _index.Register(plugin);
    }

    public ITestStepPlugin GetRequired(string pluginId, string? version = null) => _index.GetRequired(pluginId, version);

    public bool TryGet(string pluginId, out ITestStepPlugin plugin) => TryGet(pluginId, null, out plugin);

    public bool TryGet(string pluginId, string? version, out ITestStepPlugin plugin)
    {
        if (TryResolve(pluginId, version, out var resolution))
        {
            plugin = resolution.Plugin;
            return true;
        }

        plugin = null!;
        return false;
    }

    public bool TryResolve(string pluginId, string? version, out PluginResolution<ITestStepPlugin> resolution) =>
        _index.TryResolve(pluginId, version, out resolution);

    public string DescribeMissing(string pluginId, string? version) => _index.DescribeMissing(pluginId, version);
}
