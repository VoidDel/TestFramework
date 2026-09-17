using System.Reflection;

namespace TestFramework.Core.Plugins;

/// <summary>
/// Keeps one <see cref="PluginAssemblyLoadContext"/> per plugin assembly path, so the same file is
/// never loaded twice into the process. Two copies of an assembly mean two distinct sets of types:
/// a plugin loaded through one would fail every type check made against the other.
///
/// Entries are held with strong references on purpose. A registered plugin roots its own assembly
/// anyway, and a weak entry that had been collected would silently permit that second copy. An
/// assembly is dropped only through <see cref="ReleaseIfUnused"/>, when a scan proves it holds no
/// plugins at all; anything that did contribute a plugin stays loaded for the life of the process.
/// </summary>
internal static class PluginAssemblyCatalog
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, Assembly> Assemblies = new(StringComparer.OrdinalIgnoreCase);

    public static PluginAssemblyHandle Load(string assemblyPath)
    {
        var fullPath = Path.GetFullPath(assemblyPath);
        lock (Gate)
        {
            if (Assemblies.TryGetValue(fullPath, out var existing))
            {
                return new PluginAssemblyHandle(existing, null, false, fullPath);
            }

            var context = new PluginAssemblyLoadContext(fullPath);
            try
            {
                var assembly = context.LoadPluginAssembly(fullPath);
                Assemblies[fullPath] = assembly;
                return new PluginAssemblyHandle(assembly, context, true, fullPath);
            }
            catch
            {
                context.Unload();
                throw;
            }
        }
    }

    /// <summary>
    /// Unloads an assembly this call had just loaded, once the caller has established that it
    /// contributes no plugins. Callers must decide that after scanning for every plugin kind:
    /// releasing on behalf of one kind alone would unload an assembly another kind still wants,
    /// and the reload that follows would produce a second copy of its types.
    /// </summary>
    public static void ReleaseIfUnused(PluginAssemblyHandle handle)
    {
        if (!handle.IsNew || handle.LoadContext is null)
        {
            return;
        }

        lock (Gate)
        {
            if (Assemblies.TryGetValue(handle.FullPath, out var assembly) &&
                ReferenceEquals(assembly, handle.Assembly))
            {
                Assemblies.Remove(handle.FullPath);
            }
        }

        handle.LoadContext.Unload();
    }
}

internal readonly record struct PluginAssemblyHandle(
    Assembly Assembly,
    PluginAssemblyLoadContext? LoadContext,
    bool IsNew,
    string FullPath);
