using Avalonia.Controls;
using TestFramework.Plugin.Abstractions.UI;

namespace TestFramework.App.Services;

public sealed class PluginSettingsEditorRegistry
{
    private readonly Dictionary<EditorKey, Func<object, ISettingsEditContext, Control>> _factories = new();

    public void Register(
        string pluginId,
        Version version,
        Func<object, ISettingsEditContext, Control> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(factory);

        _factories[new EditorKey(pluginId, version)] = factory;
    }

    public bool TryCreateEditor(
        string pluginId,
        string? version,
        object settings,
        ISettingsEditContext context,
        out Control editor)
    {
        editor = null!;
        if (!Version.TryParse(version, out var parsedVersion))
        {
            return false;
        }

        if (!_factories.TryGetValue(new EditorKey(pluginId, parsedVersion), out var factory))
        {
            return false;
        }

        editor = factory(settings, context);
        return true;
    }

    private readonly record struct EditorKey(string PluginId, Version Version);
}
