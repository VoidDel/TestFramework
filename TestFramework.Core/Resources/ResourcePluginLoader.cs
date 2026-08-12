using System.Reflection;
using TestFramework.Abstractions.Resources;
using TestFramework.Core.Plugins;

namespace TestFramework.Core.Resources;

public sealed class ResourcePluginLoader
{
    private readonly ResourcePluginRegistry _registry;

    public ResourcePluginLoader(ResourcePluginRegistry registry)
    {
        _registry = registry;
    }

    public ResourcePluginLoadReport LoadFromDirectory(string directory)
    {
        var report = new ResourcePluginLoadReport();
        if (!Directory.Exists(directory))
        {
            return report;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories))
        {
            PluginAssemblyHandle? handle = null;
            var pluginCountBefore = CountPlugins(report);
            try
            {
                handle = PluginAssemblyCatalog.Load(file);
                LoadFromAssembly(handle.Value.Assembly, file, report);
            }
            catch (Exception ex)
            {
                AddFailure(report, file, ex);
            }
            finally
            {
                if (handle.HasValue && CountPlugins(report) == pluginCountBefore)
                {
                    PluginAssemblyCatalog.ReleaseIfUnused(handle.Value);
                }
            }
        }

        return report;
    }

    public ResourcePluginLoadReport LoadFromAssembly(Assembly assembly)
    {
        var report = new ResourcePluginLoadReport();
        var assemblyPath = assembly.Location;
        try
        {
            LoadFromAssembly(assembly, assemblyPath, report);
        }
        catch (Exception ex)
        {
            AddFailure(report, assemblyPath, ex);
        }

        return report;
    }

    private void LoadFromAssembly(Assembly assembly, string assemblyPath, ResourcePluginLoadReport report)
    {
        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface)
            {
                continue;
            }

            CreateAndRegister<IInstrumentDriverPlugin>(
                type,
                assemblyPath,
                report,
                plugin =>
                {
                    _registry.RegisterInstrumentDriver(plugin);
                    report.InstrumentDrivers.Add(plugin);
                });

            CreateAndRegister<ITransportPlugin>(
                type,
                assemblyPath,
                report,
                plugin =>
                {
                    _registry.RegisterTransport(plugin);
                    report.Transports.Add(plugin);
                });

            CreateAndRegister<ITestServicePlugin>(
                type,
                assemblyPath,
                report,
                plugin =>
                {
                    _registry.RegisterService(plugin);
                    report.Services.Add(plugin);
                });
        }
    }

    private static void CreateAndRegister<TPlugin>(
        Type type,
        string assemblyPath,
        ResourcePluginLoadReport report,
        Action<TPlugin> register)
    {
        if (!typeof(TPlugin).IsAssignableFrom(type))
        {
            return;
        }

        try
        {
            if (Activator.CreateInstance(type) is TPlugin plugin)
            {
                register(plugin);
            }
        }
        catch (Exception ex)
        {
            AddFailure(report, assemblyPath, ex);
        }
    }

    private static void AddFailure(ResourcePluginLoadReport report, string assemblyPath, Exception ex)
    {
        report.Failures.Add(new PluginLoadFailure
        {
            AssemblyPath = assemblyPath,
            Message = ex.Message,
            Exception = ex
        });
    }

    private static int CountPlugins(ResourcePluginLoadReport report)
    {
        return report.InstrumentDrivers.Count + report.Transports.Count + report.Services.Count;
    }
}
