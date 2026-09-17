using Avalonia.Controls;
using TestFramework.Abstractions.Plugins;
using TestFramework.Plugin.Abstractions.UI;

namespace TestFramework.App.Services;

public sealed class PluginSettingsEditorRegistry
{
    private readonly Dictionary<string, Dictionary<Version, Func<object, ISettingsEditContext, Control>>> _factories =
        new(StringComparer.OrdinalIgnoreCase);

    public void Register(
        string pluginId,
        Version version,
        Func<object, ISettingsEditContext, Control> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(factory);

        if (!_factories.TryGetValue(pluginId, out var versions))
        {
            versions = [];
            _factories[pluginId] = versions;
        }

        versions[version] = factory;
    }

    /// <summary>
    /// Finds the editor for a step's plugin reference using the same version rule as the runner
    /// (<see cref="PluginVersionPolicy"/>). Matching exactly here while the runner substitutes a
    /// compatible version would make the editor silently vanish for a step that still runs.
    /// </summary>
    public bool TryCreateEditor(
        string pluginId,
        string? version,
        object settings,
        ISettingsEditContext context,
        out Control editor)
    {
        editor = null!;
        if (string.IsNullOrWhiteSpace(pluginId) ||
            !_factories.TryGetValue(pluginId, out var versions) ||
            !PluginVersionPolicy.TrySelect(versions.Keys, version, out var selected, out _))
        {
            return false;
        }

        editor = versions[selected](settings, context);
        return true;
    }
}
