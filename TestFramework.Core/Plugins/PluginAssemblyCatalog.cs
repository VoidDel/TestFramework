using System.Reflection;
using System.Runtime.Loader;

namespace TestFramework.Core.Plugins;

internal static class PluginAssemblyCatalog
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, WeakReference<Assembly>> Assemblies = new(StringComparer.OrdinalIgnoreCase);

    public static PluginAssemblyHandle Load(string assemblyPath)
    {
        var fullPath = Path.GetFullPath(assemblyPath);
        lock (Gate)
        {
            if (Assemblies.TryGetValue(fullPath, out var reference) && reference.TryGetTarget(out var existing))
            {
                return new PluginAssemblyHandle(existing, null, false, fullPath);
            }

            var context = new PluginAssemblyLoadContext(fullPath);
            try
            {
                var assembly = context.LoadPluginAssembly(fullPath);
                Assemblies[fullPath] = new WeakReference<Assembly>(assembly);
                return new PluginAssemblyHandle(assembly, context, true, fullPath);
            }
            catch
            {
                context.Unload();
                throw;
            }
        }
    }

    public static void ReleaseIfUnused(PluginAssemblyHandle handle)
    {
        if (!handle.IsNew || handle.LoadContext is null)
        {
            return;
        }

        lock (Gate)
        {
            if (Assemblies.TryGetValue(handle.FullPath, out var reference) &&
                reference.TryGetTarget(out var assembly) &&
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
