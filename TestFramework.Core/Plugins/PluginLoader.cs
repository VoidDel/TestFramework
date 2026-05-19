using System.Reflection;
using System.Runtime.Loader;
using TestFramework.Abstractions.Plugins;

namespace TestFramework.Core.Plugins;

public sealed class PluginLoader
{
    private readonly IPluginRegistry _registry;

    public PluginLoader(IPluginRegistry registry)
    {
        _registry = registry;
    }

    public IReadOnlyList<ITestStepPlugin> LoadFromDirectory(string directory)
    {
        return LoadFromDirectoryWithReport(directory).LoadedPlugins;
    }

    public PluginLoadReport LoadFromDirectoryWithReport(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return new PluginLoadReport();
        }

        var report = new PluginLoadReport();

        foreach (var file in Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories))
        {
            IReadOnlyList<ITestStepPlugin> plugins;
            try
            {
                plugins = LoadFromAssembly(file);
            }
            catch (Exception ex)
            {
                report.Failures.Add(new PluginLoadFailure
                {
                    AssemblyPath = file,
                    Message = ex.Message,
                    Exception = ex
                });
                continue;
            }

            foreach (var plugin in plugins)
            {
                try
                {
                    _registry.Register(plugin);
                    report.LoadedPlugins.Add(plugin);
                }
                catch (Exception ex)
                {
                    report.Failures.Add(new PluginLoadFailure
                    {
                        AssemblyPath = file,
                        Message = ex.Message,
                        Exception = ex
                    });
                }
            }
        }

        return report;
    }

    public IReadOnlyList<ITestStepPlugin> LoadFromAssembly(string assemblyPath)
    {
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
        return CreatePlugins(assembly);
    }

    public IReadOnlyList<ITestStepPlugin> LoadFromAssembly(Assembly assembly)
    {
        return CreatePlugins(assembly);
    }

    private static IReadOnlyList<ITestStepPlugin> CreatePlugins(Assembly assembly)
    {
        var pluginType = typeof(ITestStepPlugin);
        var plugins = new List<ITestStepPlugin>();

        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface || !pluginType.IsAssignableFrom(type))
            {
                continue;
            }

            if (Activator.CreateInstance(type) is ITestStepPlugin plugin)
            {
                plugins.Add(plugin);
            }
        }

        return plugins;
    }
}
