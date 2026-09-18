using System.Reflection;
using System.Runtime.Loader;
using TestFramework.Abstractions.Plugins;

namespace TestFramework.Core.Plugins;

internal sealed class PluginAssemblyLoadContext : AssemblyLoadContext
{
    private static readonly HashSet<string> SharedAssemblyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        typeof(ITestStepPlugin).Assembly.GetName().Name!,
        "TestFramework.Plugin.Abstractions.UI"
    };

    /// <summary>
    /// True for an assembly the host provides and every plugin must share rather than carry.
    ///
    /// The two contract assemblies are the obvious case. Avalonia is the less obvious one: the UI
    /// contract's <c>CreateEditor</c> returns an Avalonia <c>Control</c>, so an editor plugin that
    /// resolved Avalonia privately would implement the interface with a <c>Control</c> of a
    /// different identity - and the runtime then reports that the type "does not have an
    /// implementation". A plugin folder produced by <c>dotnet publish</c> contains the Avalonia
    /// assemblies, so this is what a plugin author hits by default without the rule.
    /// </summary>
    internal static bool IsSharedWithHost(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        return name is not null &&
               (SharedAssemblyNames.Contains(name) ||
                name.Equals("Avalonia", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Avalonia.", StringComparison.OrdinalIgnoreCase));
    }

    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _pluginDirectory;

    public PluginAssemblyLoadContext(string pluginAssemblyPath)
        : base($"TestFramework.Plugin:{Path.GetFileNameWithoutExtension(pluginAssemblyPath)}:{Guid.NewGuid():N}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginAssemblyPath);
        _pluginDirectory = Path.GetDirectoryName(pluginAssemblyPath)!;
    }

    public Assembly LoadPluginAssembly(string pluginAssemblyPath)
    {
        return LoadFromAssemblyPath(pluginAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (IsSharedWithHost(assemblyName))
        {
            // A headless host has no Avalonia to share. Returning null lets the runtime fall back
            // to the default probing, which fails with the missing assembly's name rather than
            // with a FileNotFoundException thrown out of this override.
            try
            {
                return Default.LoadFromAssemblyName(assemblyName);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }

        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        if (assemblyPath is null && assemblyName.Name is not null)
        {
            var adjacentPath = Path.Combine(_pluginDirectory, assemblyName.Name + ".dll");
            if (File.Exists(adjacentPath))
            {
                assemblyPath = adjacentPath;
            }
        }

        // Not LoadFromAssemblyPath: that would load a private copy into this context even when
        // the same file is (or will be) loaded as a plugin of its own, and two copies of one file
        // are two sets of types. See PluginAssemblyCatalog.LoadShared.
        return assemblyPath is null ? null : PluginAssemblyCatalog.LoadShared(assemblyPath);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (libraryPath is null)
        {
            var adjacentPath = Path.Combine(_pluginDirectory, unmanagedDllName);
            if (File.Exists(adjacentPath))
            {
                libraryPath = adjacentPath;
            }
        }

        return libraryPath is null ? nint.Zero : LoadUnmanagedDllFromPath(libraryPath);
    }
}
