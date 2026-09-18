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
        if (assemblyName.Name is not null && SharedAssemblyNames.Contains(assemblyName.Name))
        {
            return Default.LoadFromAssemblyName(assemblyName);
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
