using TestFramework.Abstractions.Models;

namespace TestFramework.Core.Resources;

/// <summary>
/// The resources every station on a line shares, opened once per process: one DMM behind a switch
/// matrix, one multi-channel supply, one CAN card with a channel per station.
///
/// Each <see cref="StationResourceHost"/> on the line nests its station scope inside this one, so a
/// step's lookup that misses the run and the station falls through to here, and a sequence can
/// <c>require</c> a line alias exactly as it requires a station one. The line is described in the
/// same shape as a station - a <see cref="StationConfiguration"/> - because it is one: a bench that
/// several stations happen to stand at.
///
/// <b>Rebuilding after a fault.</b> A shared instrument that errored is suspect for every station,
/// but tearing it down while another station is mid-run against it would turn one DUT's fault into
/// several. So <see cref="Invalidate"/> marks this scope and every attached station stale; each
/// station rebuilds at the start of its own next run, attaching to a fresh generation, and the old
/// generation is released only once the last station has let go of it. Exclusive leases live in one
/// table across generations, so two stations on different generations of the same physical
/// instrument still take turns.
/// </summary>
public sealed class LineResourceHost : IAsyncDisposable
{
    private readonly ResourcePluginRegistry _plugins;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ResourceLeaseTable _leases = new();
    private readonly List<StationResourceHost> _stations = [];
    private readonly List<Generation> _retired = [];
    private StationConfiguration _line;
    private Generation? _current;
    private bool _stale;
    private bool _disposed;

    private readonly IResourceLeaseProvider? _crossProcessLocks;

    /// <param name="crossProcessLocks">
    /// Where leases on line bindings with a lock name are taken across processes, for a line
    /// instrument another process also opens. Without it, leases are shared by this process only.
    /// </param>
    public LineResourceHost(StationConfiguration line, ResourcePluginRegistry plugins, IResourceLeaseProvider? crossProcessLocks = null)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(plugins);
        _line = line;
        _plugins = plugins;
        _crossProcessLocks = crossProcessLocks;
    }

    public StationConfiguration Line => _line;

    public bool IsOpen => _current is not null;

    /// <summary>True once a fault has made the open resources untrustworthy; the next station to rebuild reopens them.</summary>
    public bool IsStale => Volatile.Read(ref _stale);

    /// <summary>Opens the line's resources, or reopens them if stale. Call at start-up so the first DUT does not pay for it.</summary>
    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Marks the line's resources untrustworthy and every attached station stale. Nothing is closed
    /// here: each station reopens at the start of its next run, and the old resources go when the
    /// last station has moved off them.
    /// </summary>
    public void Invalidate()
    {
        Volatile.Write(ref _stale, true);
        StationResourceHost[] stations;
        lock (_stations)
        {
            stations = [.. _stations];
        }

        foreach (var station in stations)
        {
            station.Invalidate();
        }
    }

    /// <summary>Replaces the configuration; stations pick up the new bindings as they rebuild.</summary>
    public void Reconfigure(StationConfiguration line)
    {
        ArgumentNullException.ThrowIfNull(line);
        _line = line;
        Invalidate();
    }

    internal void Register(StationResourceHost station)
    {
        lock (_stations)
        {
            _stations.Add(station);
        }
    }

    /// <summary>The current generation's provider, with this caller counted as using it until <see cref="DetachAsync"/>.</summary>
    internal async Task<RuntimeResourceProvider> AttachAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var generation = await EnsureAsync(cancellationToken).ConfigureAwait(false);
            generation.Attached++;
            return generation.Provider;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Releases a caller's hold on <paramref name="provider"/>. A retired generation nobody holds
    /// any more is disposed here; its resources were suspect by definition, so a close that fails
    /// is expected and only logged by whoever asked for the invalidation.
    /// </summary>
    internal async Task DetachAsync(RuntimeResourceProvider provider)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        Generation? toDispose = null;
        try
        {
            var generation = _current?.Provider == provider
                ? _current
                : _retired.FirstOrDefault(candidate => candidate.Provider == provider);
            if (generation is null)
            {
                return;
            }

            generation.Attached = Math.Max(0, generation.Attached - 1);
            if (generation != _current && generation.Attached == 0)
            {
                _retired.Remove(generation);
                toDispose = generation;
            }
        }
        finally
        {
            _gate.Release();
        }

        if (toDispose is not null)
        {
            await DisposeQuietlyAsync(toDispose.Provider).ConfigureAwait(false);
        }
    }

    private async Task<Generation> EnsureAsync(CancellationToken cancellationToken)
    {
        if (_current is not null && !_stale)
        {
            return _current;
        }

        if (_current is { } previous)
        {
            _current = null;
            if (previous.Attached == 0)
            {
                await DisposeQuietlyAsync(previous.Provider).ConfigureAwait(false);
            }
            else
            {
                _retired.Add(previous);
            }
        }

        var provider = new RuntimeResourceProvider { Leases = _leases, CrossProcessLocks = _crossProcessLocks };
        try
        {
            await ResourceBindingBuilder.BuildAsync(_plugins, provider, _line.Resources, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception buildError)
        {
            await ResourceScopeCleanup.DisposeAfterFailureAsync(provider, buildError).ConfigureAwait(false);
            throw;
        }

        _current = new Generation(provider);
        Volatile.Write(ref _stale, false);
        return _current;
    }

    private static async Task DisposeQuietlyAsync(RuntimeResourceProvider provider)
    {
        try
        {
            await provider.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Reported when the fault happened; nothing here can act on it.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var providers = new List<RuntimeResourceProvider>();
        if (_current is not null)
        {
            providers.Add(_current.Provider);
        }

        providers.AddRange(_retired.Select(generation => generation.Provider));
        _current = null;
        _retired.Clear();

        var errors = new List<Exception>();
        foreach (var provider in providers)
        {
            try
            {
                await provider.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }

        _gate.Dispose();
        if (errors.Count > 0)
        {
            throw new AggregateException("Line resource cleanup failed.", errors);
        }
    }

    private sealed class Generation(RuntimeResourceProvider provider)
    {
        public RuntimeResourceProvider Provider { get; } = provider;

        public int Attached { get; set; }
    }
}
