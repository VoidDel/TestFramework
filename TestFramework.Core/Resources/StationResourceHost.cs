using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Resources;

namespace TestFramework.Core.Resources;

/// <summary>
/// The station's long-lived resources, opened once and kept open across runs.
///
/// Before this existed the only scope was the run: every DUT opened the CAN channel and closed it,
/// connected the supply and disconnected it. A USB-CAN adapter takes about a second to initialise,
/// some instruments need to warm up, and some drivers fail when opened and closed repeatedly - so
/// per-DUT lifetime was both a line-rate cost and a reliability one.
///
/// A run gets a scope nested inside this one: it builds only what the sequence declares for itself,
/// and looks through to the station for everything else. What the run opened is released when it
/// ends; what the station opened stays.
///
/// <b>Cleanup after a failure.</b> A run that fails is not by itself a reason to reopen hardware -
/// a failed limit check says nothing about the CAN channel. A run that fails *because of a
/// resource* is, because a driver that just errored may be in any state at all, and the next DUT
/// would inherit it. So a resource error marks the station scope stale, and the next run rebuilds
/// it before starting. That is the conservative choice: it costs one reconnection after a fault,
/// and it means a fault can never quietly persist across DUTs.
/// </summary>
public sealed class StationResourceHost : IAsyncDisposable
{
    private readonly ResourcePluginRegistry _plugins;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private StationConfiguration _station;
    private RuntimeResourceProvider? _session;
    private bool _stale;
    private bool _disposed;

    public StationResourceHost(StationConfiguration station, ResourcePluginRegistry plugins)
    {
        ArgumentNullException.ThrowIfNull(station);
        ArgumentNullException.ThrowIfNull(plugins);
        _station = station;
        _plugins = plugins;
    }

    public StationConfiguration Station => _station;

    /// <summary>True once a resource error has made the open resources untrustworthy.</summary>
    public bool IsStale => Volatile.Read(ref _stale);

    /// <summary>Whether the station's shared resources are currently open.</summary>
    public bool IsOpen => _session is not null;

    /// <summary>
    /// Opens the station's shared resources, or reopens them if a previous run marked them stale.
    /// Called at start-up so the first DUT does not pay for the connection, and again by
    /// <see cref="BeginRunAsync"/> whenever the scope needs rebuilding.
    /// </summary>
    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Builds the scope for one run: the station's shared resources, plus whatever the sequence
    /// declares for itself. Dispose the result when the run ends - it releases only what the run
    /// opened.
    /// </summary>
    public async Task<RuntimeResourceProvider> BeginRunAsync(
        TestSequence sequence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
            var run = new RuntimeResourceProvider(session);

            try
            {
                // The sequence's own inline definitions, plus anything it requires that the station
                // binds as unshared. A shared binding is already in the session scope and must not
                // be opened a second time.
                await new RuntimeResourceBuilder(_plugins)
                    .PopulateAsync(run, sequence, cancellationToken)
                    .ConfigureAwait(false);

                await BuildBindingsAsync(
                    run,
                    UnsharedBindingsFor(sequence),
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await run.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            return run;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Marks the station's resources untrustworthy, so the next run reopens them.
    ///
    /// Call it when a run ended in a resource error. It does not close anything itself: the
    /// operator may be looking at the failure, and tearing hardware down underneath them is worse
    /// than doing it at the start of the next run, where it is expected and can be reported.
    /// </summary>
    public void Invalidate() => Volatile.Write(ref _stale, true);

    /// <summary>Replaces the configuration; the next run picks up the new bindings.</summary>
    public void Reconfigure(StationConfiguration station)
    {
        ArgumentNullException.ThrowIfNull(station);
        _station = station;
        Invalidate();
    }

    private async Task<RuntimeResourceProvider> EnsureSessionAsync(CancellationToken cancellationToken)
    {
        if (_session is not null && !_stale)
        {
            return _session;
        }

        if (_session is not null)
        {
            // Closing the stale scope must not prevent opening a fresh one: the resources are
            // suspect by definition, so a driver that also fails to close is expected here.
            var previous = _session;
            _session = null;
            try
            {
                await previous.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Reported when it happened; nothing here can act on it.
            }
        }

        var session = new RuntimeResourceProvider();
        try
        {
            await BuildBindingsAsync(
                session,
                _station.Resources.Where(binding => binding.Shared),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        _session = session;
        Volatile.Write(ref _stale, false);
        return session;
    }

    private IEnumerable<StationResourceBinding> UnsharedBindingsFor(TestSequence sequence)
    {
        if (sequence.Requires.Count == 0)
        {
            return [];
        }

        var required = sequence.Requires
            .Select(requirement => requirement.Alias)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return _station.Resources.Where(binding => !binding.Shared && required.Contains(binding.Alias));
    }

    /// <summary>
    /// Opens bindings in dependency order - instruments, then the transports that run over them,
    /// then the services that run over those - because a transport's driver looks its instrument up
    /// through the scope while it builds.
    /// </summary>
    private async Task BuildBindingsAsync(
        RuntimeResourceProvider target,
        IEnumerable<StationResourceBinding> bindings,
        CancellationToken cancellationToken)
    {
        var ordered = bindings.ToArray();
        if (ordered.Length == 0)
        {
            return;
        }

        foreach (var binding in ordered.Where(binding => binding.Kind == ResourcePluginKind.InstrumentDriver))
        {
            var plugin = _plugins.GetRequiredInstrumentDriver(binding.DriverId, binding.DriverVersion);
            target.RegisterInstrument(
                binding.Alias,
                await plugin.CreateAsync(binding.ToInstrumentDefinition(), target.Scope, cancellationToken).ConfigureAwait(false));
        }

        foreach (var binding in ordered.Where(binding => binding.Kind == ResourcePluginKind.Transport))
        {
            var plugin = _plugins.GetRequiredTransport(binding.DriverId, binding.DriverVersion);
            target.RegisterTransport(
                binding.Alias,
                await plugin.CreateAsync(binding.ToTransportDefinition(), target.Scope, cancellationToken).ConfigureAwait(false));
        }

        foreach (var binding in ordered.Where(binding => binding.Kind == ResourcePluginKind.Service))
        {
            var plugin = _plugins.GetRequiredService(binding.DriverId, binding.DriverVersion);
            target.RegisterService(
                binding.Alias,
                await plugin.CreateAsync(binding.ToServiceDefinition(), target.Scope, cancellationToken).ConfigureAwait(false));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var session = _session;
        _session = null;
        if (session is not null)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }
}
