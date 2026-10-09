using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Resources;

namespace TestFramework.Core.Resources;

/// <summary>
/// Opens station-style bindings into a scope, in dependency order - instruments, then the
/// transports that run over them, then the services that run over those - because a transport's
/// driver looks its instrument up through the scope while it builds. Shared by the station host and
/// the line host so the two cannot open the same binding differently.
/// </summary>
internal static class ResourceBindingBuilder
{
    public static async Task BuildAsync(
        ResourcePluginRegistry plugins,
        RuntimeResourceProvider target,
        IEnumerable<StationResourceBinding> bindings,
        CancellationToken cancellationToken)
    {
        var ordered = bindings.ToArray();
        if (ordered.Length == 0)
        {
            return;
        }

        foreach (var binding in ordered.Where(binding => !string.IsNullOrWhiteSpace(binding.LockName)))
        {
            target.RegisterLockName(binding.Alias, binding.LockName!);
        }

        foreach (var binding in ordered.Where(binding => binding.Kind == ResourcePluginKind.InstrumentDriver))
        {
            var plugin = plugins.GetRequiredInstrumentDriver(binding.DriverId, binding.DriverVersion);
            using var turn = await ResourceCreationGate.EnterAsync(plugin, cancellationToken).ConfigureAwait(false);
            target.RegisterInstrument(
                binding.Alias,
                await plugin.CreateAsync(binding.ToInstrumentDefinition(), target.Scope, cancellationToken).ConfigureAwait(false));
        }

        foreach (var binding in ordered.Where(binding => binding.Kind == ResourcePluginKind.Transport))
        {
            var plugin = plugins.GetRequiredTransport(binding.DriverId, binding.DriverVersion);
            using var turn = await ResourceCreationGate.EnterAsync(plugin, cancellationToken).ConfigureAwait(false);
            target.RegisterTransport(
                binding.Alias,
                await plugin.CreateAsync(binding.ToTransportDefinition(), target.Scope, cancellationToken).ConfigureAwait(false));
        }

        foreach (var binding in ordered.Where(binding => binding.Kind == ResourcePluginKind.Service))
        {
            var plugin = plugins.GetRequiredService(binding.DriverId, binding.DriverVersion);
            using var turn = await ResourceCreationGate.EnterAsync(plugin, cancellationToken).ConfigureAwait(false);
            target.RegisterService(
                binding.Alias,
                await plugin.CreateAsync(binding.ToServiceDefinition(), target.Scope, cancellationToken).ConfigureAwait(false));
        }
    }
}
