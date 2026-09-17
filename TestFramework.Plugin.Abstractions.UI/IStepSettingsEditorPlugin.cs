using Avalonia.Controls;

namespace TestFramework.Plugin.Abstractions.UI;

/// <summary>
/// Supplies the settings editor for one step plugin version.
///
/// The editor names the plugin it serves rather than being implemented by the plugin itself, so it
/// can live in its own assembly - conventionally <c>MyPlugin.UI.dll</c> beside <c>MyPlugin.dll</c>.
/// That keeps Avalonia out of the runtime plugin, which matters because a headless or CI host loads
/// the runtime plugin to execute sequences and never has a UI to show. A plugin that does not care
/// about headless hosts can still implement this on the plugin type itself; the host discovers it
/// the same way either way.
///
/// <see cref="PluginVersion"/> is matched with the same version rule the runner uses, so an editor
/// registered for 1.0.0 also serves a step pinned to an earlier 1.x that resolved forward to it.
/// </summary>
public interface IStepSettingsEditorPlugin
{
    /// <summary>The <c>PluginId</c> of the step plugin this editor belongs to.</summary>
    string PluginId { get; }

    /// <summary>The step plugin version this editor was written against.</summary>
    Version PluginVersion { get; }

    /// <summary>
    /// Builds the editor for <paramref name="settings"/>, an instance of the plugin's settings type.
    /// Call <see cref="ISettingsEditContext.NotifySettingsChanged"/> after mutating it.
    /// </summary>
    Control CreateEditor(object settings, ISettingsEditContext context);
}
