namespace TestFramework.Core.Plugins;

/// <summary>
/// Discovers one additional kind of plugin during a directory scan.
///
/// The core scan knows about step plugins and resource plugins. A host layer may define kinds of its
/// own - the desktop app looks for settings editors, which live above Core because they are
/// Avalonia types - and those must be found in the same pass: a second walk would reload every
/// assembly and hand the host a different copy of each type.
/// </summary>
public interface IPluginTypeHandler
{
    /// <summary>
    /// Instantiates and registers this handler's plugins from <paramref name="types"/>, recording
    /// per-type problems in <paramref name="failures"/>. Returns how many were registered, which is
    /// how the loader knows whether the assembly contributed anything at all.
    /// </summary>
    int Register(IReadOnlyList<Type> types, string assemblyPath, ICollection<PluginLoadFailure> failures);
}
