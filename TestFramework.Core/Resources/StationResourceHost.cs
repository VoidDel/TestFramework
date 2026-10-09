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
    private readonly LineResourceHost? _line;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private StationConfiguration _station;
    private RuntimeResourceProvider? _session;
    private RuntimeResourceProvider? _sessionParent;
    private bool _stale;
    private bool _disposed;

    /// <param name="line">
    /// The line this station stands on, when several stations share instruments. The station scope
    /// nests inside the line's, so a lookup that misses here reaches the shared instrument, and the
    /// line's <see cref="LineResourceHost.Invalidate"/> marks this station stale too.
    /// </param>
    public StationResourceHost(StationConfiguration station, ResourcePluginRegistry plugins, LineResourceHost? line = null)
    {
        ArgumentNullException.ThrowIfNull(station);
        ArgumentNullException.ThrowIfNull(plugins);
        _station = station;
        _plugins = plugins;
        _line = line;
        line?.Register(this);
    }

    public StationConfiguration Station => _station;

    public LineResourceHost? Line => _line;

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

                await ResourceBindingBuilder.BuildAsync(
                    _plugins,
                    run,
                    UnsharedBindingsFor(sequence),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception buildError)
            {
                await ResourceScopeCleanup.DisposeAfterFailureAsync(run, buildError).ConfigureAwait(false);
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

            await ReleaseLineAsync().ConfigureAwait(false);
        }

        // Attached to the line's current generation; a line that is itself stale rebuilds here, on
        // the first station to come back, and the others follow at their own next run.
        var parent = _line is null ? null : await _line.AttachAsync(cancellationToken).ConfigureAwait(false);
        var session = parent is null ? new RuntimeResourceProvider() : new RuntimeResourceProvider(parent);
        try
        {
            await ResourceBindingBuilder.BuildAsync(
                _plugins,
                session,
                _station.Resources.Where(binding => binding.Shared),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception buildError)
        {
            await ResourceScopeCleanup.DisposeAfterFailureAsync(session, buildError).ConfigureAwait(false);
            if (parent is not null)
            {
                await _line!.DetachAsync(parent).ConfigureAwait(false);
            }

            throw;
        }

        _session = session;
        _sessionParent = parent;
        Volatile.Write(ref _stale, false);
        return session;
    }

    private async Task ReleaseLineAsync()
    {
        if (_sessionParent is { } parent)
        {
            _sessionParent = null;
            await _line!.DetachAsync(parent).ConfigureAwait(false);
        }
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

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var session = _session;
        _session = null;
        try
        {
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            await ReleaseLineAsync().ConfigureAwait(false);
            _gate.Dispose();
        }
    }
}
