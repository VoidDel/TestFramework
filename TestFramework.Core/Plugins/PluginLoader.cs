using System.Reflection;
using TestFramework.Abstractions.Plugins;

namespace TestFramework.Core.Plugins;

/// <summary>
/// Loads step plugins from individual assemblies. Scanning a whole plugin directory goes through
/// <see cref="PluginDirectoryLoader"/> instead, which walks it once for every plugin kind; a
/// step-only directory scan cannot tell a resource-only assembly from an empty one.
/// </summary>
public sealed class PluginLoader
{
    private readonly IPluginRegistry _registry;

    public PluginLoader(IPluginRegistry registry)
    {
        _registry = registry;
    }

    public IReadOnlyList<ITestStepPlugin> LoadFromAssembly(string assemblyPath)
    {
        return LoadFromAssembly(assemblyPath, null);
    }

    private static IReadOnlyList<ITestStepPlugin> LoadFromAssembly(string assemblyPath, PluginLoadReport? report)
    {
        var handle = PluginAssemblyCatalog.Load(assemblyPath);
        try
        {
            var plugins = CreatePlugins(handle.Assembly, report, assemblyPath);
            if (plugins.Count == 0)
            {
                PluginAssemblyCatalog.ReleaseIfUnused(handle);
            }

            return plugins;
        }
        catch
        {
            PluginAssemblyCatalog.ReleaseIfUnused(handle);
            throw;
        }
    }

    public IReadOnlyList<ITestStepPlugin> LoadFromAssembly(Assembly assembly)
    {
        return CreatePlugins(assembly);
    }

    /// <summary>
    /// Loads the plugins of an already-loaded assembly, isolating per-type failures into the
    /// returned report instead of discarding the assembly's remaining plugins.
    /// </summary>
    public PluginLoadReport LoadFromAssemblyWithReport(Assembly assembly)
    {
        var report = new PluginLoadReport();
        var assemblyPath = assembly.Location;

        foreach (var plugin in CreatePlugins(assembly, report, assemblyPath))
        {
            try
            {
                _registry.Register(plugin);
                report.LoadedPlugins.Add(plugin);
                report.PluginPaths[plugin] = assemblyPath;
            }
            catch (Exception ex)
            {
                report.Failures.Add(new PluginLoadFailure
                {
                    AssemblyPath = assemblyPath,
                    Message = ex.Message,
                    Exception = ex
                });
            }
        }

        return report;
    }

    private static IReadOnlyList<ITestStepPlugin> CreatePlugins(Assembly assembly)
    {
        return CreatePlugins(assembly, null, null);
    }

    private static IReadOnlyList<ITestStepPlugin> CreatePlugins(
        Assembly assembly,
        PluginLoadReport? report,
        string? assemblyPath)
    {
        var path = assemblyPath ?? assembly.Location;
        var types = PluginAssemblyScan.GetLoadableTypes(assembly, out var typeLoadFailure);
        if (typeLoadFailure is not null)
        {
            if (report is null)
            {
                throw typeLoadFailure;
            }

            report.Failures.Add(new PluginLoadFailure
            {
                AssemblyPath = path,
                Message = typeLoadFailure.Message,
                Exception = typeLoadFailure
            });
        }

        return PluginActivator.CreateAll<ITestStepPlugin>(types, path, report?.Failures);
    }
}
