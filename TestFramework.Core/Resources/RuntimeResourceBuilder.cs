using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Resources;

namespace TestFramework.Core.Resources;

public sealed class RuntimeResourceBuilder
{
    private readonly ResourcePluginRegistry _plugins;

    public RuntimeResourceBuilder(ResourcePluginRegistry plugins)
    {
        _plugins = plugins;
    }

    public async Task<RuntimeResourceProvider> BuildAsync(
        TestSequence sequence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sequence);

        // Resolve every plugin before opening the first resource.
        foreach (var definition in sequence.Instruments) _plugins.GetRequiredInstrumentDriver(definition.DriverId, definition.DriverVersion);
        foreach (var definition in sequence.Transports) _plugins.GetRequiredTransport(definition.TransportId, definition.TransportVersion);
        foreach (var definition in sequence.Services) _plugins.GetRequiredService(definition.ServiceId, definition.ServiceVersion);

        var resources = new RuntimeResourceProvider();

        try
        {
            await PopulateAsync(resources, sequence, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception buildError)
        {
            await ResourceScopeCleanup.DisposeAfterFailureAsync(resources, buildError).ConfigureAwait(false);
            throw;
        }

        return resources;
    }

    /// <summary>
    /// Opens the sequence's own resources into an existing scope, leaving ownership with the
    /// caller. <see cref="StationResourceHost"/> uses it to fill a run scope nested inside the
    /// station's, so both paths open inline definitions exactly the same way; on failure the
    /// caller disposes the scope it owns.
    /// </summary>
    public async Task PopulateAsync(
        RuntimeResourceProvider resources,
        TestSequence sequence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(sequence);

        foreach (var definition in sequence.Instruments)
        {
            var plugin = _plugins.GetRequiredInstrumentDriver(definition.DriverId, definition.DriverVersion);
            resources.RegisterInstrument(
                definition.Id,
                await plugin.CreateAsync(definition, resources.Scope, cancellationToken).ConfigureAwait(false));
        }

        foreach (var definition in sequence.Transports)
        {
            var plugin = _plugins.GetRequiredTransport(definition.TransportId, definition.TransportVersion);
            resources.RegisterTransport(
                definition.Id,
                await plugin.CreateAsync(definition, resources.Scope, cancellationToken).ConfigureAwait(false));
        }

        foreach (var definition in sequence.Services)
        {
            var plugin = _plugins.GetRequiredService(definition.ServiceId, definition.ServiceVersion);
            resources.RegisterService(
                definition.Id,
                await plugin.CreateAsync(definition, resources.Scope, cancellationToken).ConfigureAwait(false));
        }
    }
}
