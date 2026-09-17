using TestFramework.Abstractions.Plugins;
using TestFramework.Abstractions.Resources;
using TestFramework.Core.Resources;

namespace TestFramework.Core.Plugins;

/// <summary>
/// Loads every plugin in a directory - step plugins and resource plugins alike - in a single pass.
///
/// Running the two loaders separately walks the directory twice, parses every DLL header twice and
/// reflects over every assembly twice. Worse, the first pass cannot tell a resource-only assembly
/// from one holding nothing, so it unloads it and the second pass loads it again. Scanning once and
/// deciding afterwards removes that churn and is the only point at which "this assembly contributes
/// nothing" is actually knowable.
/// </summary>
public sealed class PluginDirectoryLoader
{
    private readonly IPluginRegistry _stepPlugins;
    private readonly ResourcePluginRegistry _resourcePlugins;
    private readonly IReadOnlyList<IPluginTypeHandler> _extraHandlers;

    public PluginDirectoryLoader(
        IPluginRegistry stepPlugins,
        ResourcePluginRegistry resourcePlugins,
        IEnumerable<IPluginTypeHandler>? extraHandlers = null)
    {
        ArgumentNullException.ThrowIfNull(stepPlugins);
        ArgumentNullException.ThrowIfNull(resourcePlugins);
        _stepPlugins = stepPlugins;
        _resourcePlugins = resourcePlugins;
        _extraHandlers = extraHandlers?.ToArray() ?? [];
    }

    public PluginDirectoryLoadReport LoadFromDirectory(string directory)
    {
        var report = new PluginDirectoryLoadReport();
        if (!Directory.Exists(directory))
        {
            return report;
        }

        foreach (var file in PluginAssemblyScan.EnumerateCandidateAssemblies(directory))
        {
            LoadAssembly(file, report);
        }

        return report;
    }

    private void LoadAssembly(string file, PluginDirectoryLoadReport report)
    {
        PluginAssemblyHandle handle;
        try
        {
            handle = PluginAssemblyCatalog.Load(file);
        }
        catch (Exception ex)
        {
            report.AddFailure(file, ex);
            return;
        }

        var loadedBefore = report.LoadedCount;
        var extraRegistered = 0;
        try
        {
            // Checked before any plugin type is constructed: a plugin needing a contract this build
            // does not have must be refused outright, not left to fail as a missing member somewhere
            // inside a run.
            var requiredContract = PluginContractReader.ReadMinimumFrameworkVersion(handle.Assembly);
            report.AssemblyContracts[file] = requiredContract;
            if (!FrameworkContract.Supports(requiredContract))
            {
                report.AddFailure(file, new NotSupportedException(
                    $"Plugin requires framework contract {requiredContract} but this build provides {FrameworkContract.Version}. Update the host, or rebuild the plugin against contract {FrameworkContract.Version}."));
                return;
            }

            var types = PluginAssemblyScan.GetLoadableTypes(handle.Assembly, out var typeLoadFailure);
            if (typeLoadFailure is not null)
            {
                report.AddFailure(file, typeLoadFailure);
            }

            RegisterStepPlugins(types, file, report);
            RegisterResourcePlugins(types, file, report);

            foreach (var handler in _extraHandlers)
            {
                try
                {
                    extraRegistered += handler.Register(types, file, report.Failures);
                }
                catch (Exception ex)
                {
                    report.AddFailure(file, ex);
                }
            }
        }
        catch (Exception ex)
        {
            report.AddFailure(file, ex);
        }
        finally
        {
            if (report.LoadedCount == loadedBefore && extraRegistered == 0)
            {
                PluginAssemblyCatalog.ReleaseIfUnused(handle);
            }
        }
    }

    private void RegisterStepPlugins(IReadOnlyList<Type> types, string file, PluginDirectoryLoadReport report)
    {
        foreach (var plugin in PluginActivator.CreateAll<ITestStepPlugin>(types, file, report.Failures))
        {
            Register(() => _stepPlugins.Register(plugin), plugin, file, report, report.StepPlugins);
        }
    }

    private void RegisterResourcePlugins(IReadOnlyList<Type> types, string file, PluginDirectoryLoadReport report)
    {
        foreach (var plugin in PluginActivator.CreateAll<IInstrumentDriverPlugin>(types, file, report.Failures))
        {
            Register(() => _resourcePlugins.RegisterInstrumentDriver(plugin), plugin, file, report, report.InstrumentDrivers);
        }

        foreach (var plugin in PluginActivator.CreateAll<ITransportPlugin>(types, file, report.Failures))
        {
            Register(() => _resourcePlugins.RegisterTransport(plugin), plugin, file, report, report.Transports);
        }

        foreach (var plugin in PluginActivator.CreateAll<ITestServicePlugin>(types, file, report.Failures))
        {
            Register(() => _resourcePlugins.RegisterService(plugin), plugin, file, report, report.Services);
        }
    }

    private static void Register<TPlugin>(
        Action register,
        TPlugin plugin,
        string file,
        PluginDirectoryLoadReport report,
        ICollection<TPlugin> loaded)
        where TPlugin : class
    {
        // A duplicate id or version is rejected by the registry; that is this assembly's problem,
        // not the directory's, so it is recorded and the scan continues.
        try
        {
            register();
        }
        catch (Exception ex)
        {
            report.AddFailure(file, ex);
            return;
        }

        loaded.Add(plugin);
        report.PluginPaths[plugin] = file;
    }
}
