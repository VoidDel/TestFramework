using System.Reflection;
using TestFramework.Abstractions.Resources;
using TestFramework.Core.Plugins;

namespace TestFramework.Core.Resources;

/// <summary>
/// Loads resource plugins from individual assemblies. Directory scanning lives in
/// <see cref="TestFramework.Core.Plugins.PluginDirectoryLoader"/>, which covers both plugin kinds
/// in one pass.
/// </summary>
public sealed class ResourcePluginLoader
{
    private readonly ResourcePluginRegistry _registry;

    public ResourcePluginLoader(ResourcePluginRegistry registry)
    {
        _registry = registry;
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
        var types = PluginAssemblyScan.GetLoadableTypes(assembly, out var typeLoadFailure);
        if (typeLoadFailure is not null)
        {
            AddFailure(report, assemblyPath, typeLoadFailure);
        }

        RegisterAll(types, assemblyPath, report);
    }

    /// <summary>
    /// Instantiates and registers every resource plugin among <paramref name="types"/>. A single
    /// type may implement more than one resource contract, so each kind is scanned independently.
    /// </summary>
    internal void RegisterAll(IReadOnlyList<Type> types, string assemblyPath, ResourcePluginLoadReport report)
    {
        foreach (var plugin in PluginActivator.CreateAll<IInstrumentDriverPlugin>(types, assemblyPath, report.Failures))
        {
            Register(() => _registry.RegisterInstrumentDriver(plugin), () => report.InstrumentDrivers.Add(plugin), assemblyPath, report);
        }

        foreach (var plugin in PluginActivator.CreateAll<ITransportPlugin>(types, assemblyPath, report.Failures))
        {
            Register(() => _registry.RegisterTransport(plugin), () => report.Transports.Add(plugin), assemblyPath, report);
        }

        foreach (var plugin in PluginActivator.CreateAll<ITestServicePlugin>(types, assemblyPath, report.Failures))
        {
            Register(() => _registry.RegisterService(plugin), () => report.Services.Add(plugin), assemblyPath, report);
        }
    }

    private static void Register(Action register, Action record, string assemblyPath, ResourcePluginLoadReport report)
    {
        try
        {
            register();
            record();
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
}
