using TestFramework.Abstractions.Plugins;

namespace TestFramework.Core.Plugins;

public sealed class PluginRegistry : IPluginRegistry
{
    private readonly VersionedPluginIndex<ITestStepPlugin> _index = new(
        "Test step plugin",
        plugin => plugin.Descriptor.PluginId,
        plugin => plugin.Descriptor.Version);

    public IReadOnlyCollection<ITestStepPlugin> Plugins => _index.Plugins
        .OrderBy(plugin => plugin.Descriptor.Category)
        .ThenBy(plugin => plugin.Descriptor.DisplayName)
        .ThenByDescending(plugin => plugin.Descriptor.Version)
        .ToArray();

    public void Register(ITestStepPlugin plugin) => _index.Register(plugin);

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
