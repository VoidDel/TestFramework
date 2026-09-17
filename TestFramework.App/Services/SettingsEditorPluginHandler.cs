using TestFramework.Core.Plugins;
using TestFramework.Plugin.Abstractions.UI;

namespace TestFramework.App.Services;

/// <summary>
/// Finds settings editors while the core scan walks the plugin directory.
///
/// Editors are Avalonia types, so Core cannot look for them; but they live in the same assemblies as
/// everything else, and a separate pass would load those assemblies a second time and hand the app a
/// different copy of each type. This plugs into the one scan instead.
/// </summary>
internal sealed class SettingsEditorPluginHandler : IPluginTypeHandler
{
    private readonly PluginSettingsEditorRegistry _registry;

    public SettingsEditorPluginHandler(PluginSettingsEditorRegistry registry)
    {
        _registry = registry;
    }

    public int Register(IReadOnlyList<Type> types, string assemblyPath, ICollection<PluginLoadFailure> failures)
    {
        var registered = 0;
        foreach (var editor in PluginActivator.CreateAll<IStepSettingsEditorPlugin>(types, assemblyPath, failures))
        {
            try
            {
                _registry.Register(editor.PluginId, editor.PluginVersion, editor.CreateEditor);
                registered++;
            }
            catch (Exception ex)
            {
                failures.Add(new PluginLoadFailure
                {
                    AssemblyPath = assemblyPath,
                    Message = $"{editor.GetType().FullName}: {ex.Message}",
                    Exception = ex
                });
            }
        }

        return registered;
    }
}
