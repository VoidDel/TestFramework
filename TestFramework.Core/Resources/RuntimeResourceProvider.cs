using TestFramework.Abstractions.Resources;

namespace TestFramework.Core.Resources;

/// <summary>
/// The host-owned container for a run's resources. It lives in Core rather than beside the plugin
/// contracts because registering and disposing resources is the host's job: plugins receive the
/// read-only <see cref="IResourceScope"/> instead.
/// </summary>
public sealed class RuntimeResourceProvider :
    IResourceScope,
    IInstrumentProvider,
    ITransportProvider,
    ITestServiceProvider,
    IDisposable,
    IAsyncDisposable
{
    public static RuntimeResourceProvider Empty { get; } = new(isReadOnly: true);

    public IInstrumentProvider Instruments => this;

    public ITransportProvider Transports => this;

    public ITestServiceProvider Services => this;

    /// <summary>
    /// What plugin code is given instead of this object. This provider is also the disposable
    /// container, and a plugin holding it could release the run's resources through a cast; the
    /// scope forwards lookups only.
    /// </summary>
    public ReadOnlyResourceScope Scope => _scope ??= new ReadOnlyResourceScope(this);

    /// <summary>
    /// The exclusive-use locks for the resources this provider owns. A host that rebuilds its
    /// provider across generations passes the same table to each, so the lock outlives the handle.
    /// </summary>
    public ResourceLeaseTable Leases { get; init; } = new();

    /// <summary>
    /// Where the leases on resources registered with a lock name (<see cref="RegisterLockName"/>)
    /// are taken across processes. Null leaves those leases in-process, which is right for a host
    /// that is the only process on its bench.
    /// </summary>
    public IResourceLeaseProvider? CrossProcessLocks { get; init; }

    private readonly Dictionary<string, string> _lockNames = new(StringComparer.OrdinalIgnoreCase);

    private ReadOnlyResourceScope? _scope;
    private readonly bool _isReadOnly;
    private readonly RuntimeResourceProvider? _parent;
    private readonly Dictionary<string, object> _instruments = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, object> _transports = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, object> _services = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public RuntimeResourceProvider()
    {
    }

    /// <summary>
    /// A provider that falls back to <paramref name="parent"/> for anything it does not hold itself.
    ///
    /// This is what lets a run scope sit inside a station scope. The run builds only what the
    /// sequence adds; a lookup that misses reaches the station's long-lived resources, so a step
    /// asks for "psu" without knowing or caring which scope opened it. Disposal stops here: the run
    /// releases what it opened and leaves the station's alone, which is the entire point of keeping
    /// a CAN channel open between DUTs.
    /// </summary>
    public RuntimeResourceProvider(RuntimeResourceProvider parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        _parent = parent;
    }

    private RuntimeResourceProvider(bool isReadOnly)
    {
        _isReadOnly = isReadOnly;
    }

    public void RegisterInstrument(string id, object instrument)
    {
        Register(_instruments, id, instrument);
    }

    public void RegisterTransport(string id, object transport)
    {
        Register(_transports, id, transport);
    }

    public void RegisterService(string id, object service)
    {
        Register(_services, id, service);
    }

    /// <summary>
    /// Waits for exclusive use of the resource <paramref name="alias"/> names, wherever in the chain
    /// of scopes it was opened - the lock is the owner's, so two stations whose scopes both fall
    /// through to one line instrument contend for the same lock. An alias nothing provides is an
    /// error rather than a free lock: a misspelt <c>exclusive</c> would otherwise protect nothing.
    /// </summary>
    public Task<IDisposable> LeaseAsync(string alias, CancellationToken cancellationToken) =>
        LeaseAsync([alias], cancellationToken);

    /// <summary>
    /// Exclusive use of every alias at once: each one's in-process lease, then - for an alias
    /// registered with a lock name - the cross-process lease on that name.
    ///
    /// The order is what keeps this free of deadlock. In-process leases are taken first, in alias
    /// order; they only ever contend with this process. Cross-process leases come last, ordered by
    /// <i>lock name</i>, not alias: two stations may call one instrument by different aliases, and
    /// only the name they share is an order both processes agree on. Nothing waits for an
    /// in-process lease while holding a cross-process one, so no cycle can span the two.
    /// </summary>
    public async Task<IDisposable> LeaseAsync(IReadOnlyCollection<string> aliases, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(aliases);
        var owners = aliases
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Select(alias => alias.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(alias => (Alias: alias, Owner: OwnerOf(alias)))
            .ToList();

        var held = new List<IDisposable>(owners.Count * 2);
        try
        {
            foreach (var (alias, owner) in owners.OrderBy(pair => pair.Alias, StringComparer.OrdinalIgnoreCase))
            {
                held.Add(await owner.Leases.LeaseAsync(alias, cancellationToken).ConfigureAwait(false));
            }

            var crossProcess = owners
                .Where(pair => pair.Owner.CrossProcessLocks is not null && pair.Owner._lockNames.ContainsKey(pair.Alias))
                .Select(pair => (Name: pair.Owner._lockNames[pair.Alias], Provider: pair.Owner.CrossProcessLocks!))
                .DistinctBy(pair => pair.Name, StringComparer.OrdinalIgnoreCase)
                .OrderBy(pair => pair.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var (name, provider) in crossProcess)
            {
                held.Add(await provider.LeaseAsync(name, cancellationToken).ConfigureAwait(false));
            }

            return new LeaseSet(held);
        }
        catch
        {
            new LeaseSet(held).Dispose();
            throw;
        }
    }

    /// <summary>
    /// Marks <paramref name="alias"/> as a physical instrument other processes also open, under
    /// <paramref name="lockName"/>. Its exclusive leases are then taken across processes too, through
    /// <see cref="CrossProcessLocks"/>.
    /// </summary>
    public void RegisterLockName(string alias, string lockName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        ArgumentException.ThrowIfNullOrWhiteSpace(lockName);
        _lockNames[alias] = lockName.Trim();
    }

    /// <summary>
    /// The scope that opened <paramref name="alias"/>, wherever in the chain: the lock is the owner's.
    /// An alias nothing provides is an error rather than a free lock - a misspelt <c>exclusive</c>
    /// would otherwise protect nothing.
    /// </summary>
    private RuntimeResourceProvider OwnerOf(string alias)
    {
        for (var scope = this; scope is not null; scope = scope._parent)
        {
            if (scope.Owns(alias))
            {
                return scope;
            }
        }

        throw new InvalidOperationException($"Resource '{alias}' is not available, so it cannot be used exclusively.");
    }

    private sealed class LeaseSet(List<IDisposable> held) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            for (var index = held.Count - 1; index >= 0; index--)
            {
                held[index].Dispose();
            }
        }
    }

    private bool Owns(string alias) =>
        _instruments.ContainsKey(alias) || _transports.ContainsKey(alias) || _services.ContainsKey(alias);

    T IInstrumentProvider.GetRequired<T>(string id) => GetRequired<T>(id, "instrument", TryGetInstrument);

    bool IInstrumentProvider.TryGet<T>(string id, out T instrument) => TryGetInstrument(id, out instrument);

    T ITransportProvider.GetRequired<T>(string id) => GetRequired<T>(id, "transport", TryGetTransport);

    bool ITransportProvider.TryGet<T>(string id, out T transport) => TryGetTransport(id, out transport);

    T ITestServiceProvider.GetRequired<T>(string id) => GetRequired<T>(id, "service", TryGetService);

    bool ITestServiceProvider.TryGet<T>(string id, out T service) => TryGetService(id, out service);

    // Own resources shadow the parent's: a sequence that opens its own "psu" gets that one, not the
    // station's. Shadowing rather than colliding is what lets a sequence override a station
    // resource for one run without the station having to know.
    private bool TryGetInstrument<T>(string id, out T value)
        where T : class =>
        TryGet(_instruments, id, out value) ||
        (_parent is not null && ((IInstrumentProvider)_parent).TryGet(id, out value));

    private bool TryGetTransport<T>(string id, out T value)
        where T : class =>
        TryGet(_transports, id, out value) ||
        (_parent is not null && ((ITransportProvider)_parent).TryGet(id, out value));

    private bool TryGetService<T>(string id, out T value)
        where T : class =>
        TryGet(_services, id, out value) ||
        (_parent is not null && ((ITestServiceProvider)_parent).TryGet(id, out value));

    private delegate bool TryGetResource<T>(string id, out T value)
        where T : class;

    private static T GetRequired<T>(string id, string kind, TryGetResource<T> tryGet)
        where T : class
    {
        if (tryGet(id, out var value))
        {
            return value;
        }

        throw new InvalidOperationException($"Runtime {kind} '{id}' of type '{typeof(T).Name}' is not available.");
    }

    private static void Register(IDictionary<string, object> target, string id, object value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(value);
        target[id] = value;
    }

    private void Register(Dictionary<string, object> target, string id, object value)
    {
        if (_isReadOnly)
        {
            throw new InvalidOperationException("The shared empty runtime resource provider cannot be modified.");
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        Register((IDictionary<string, object>)target, id, value);
    }

    private static bool TryGet<T>(IReadOnlyDictionary<string, object> source, string id, out T value)
        where T : class
    {
        value = null!;
        if (!source.TryGetValue(id, out var candidate) || candidate is not T typed)
        {
            return false;
        }

        value = typed;
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var errors = new List<Exception>();
        foreach (var resource in GetDisposableResources())
        {
            try
            {
                if (resource is IDisposable disposable)
                {
                    disposable.Dispose();
                }
                else if (resource is IAsyncDisposable asyncDisposable)
                {
                    asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }

        if (errors.Count > 0) throw new AggregateException("Runtime resource cleanup failed.", errors);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var errors = new List<Exception>();
        foreach (var resource in GetDisposableResources())
        {
            try
            {
                if (resource is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                }
                else if (resource is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }

        if (errors.Count > 0) throw new AggregateException("Runtime resource cleanup failed.", errors);
    }

    private IReadOnlyList<object> GetDisposableResources()
    {
        var resources = new List<object>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);

        AddResources(_services.Values, resources, seen);
        AddResources(_transports.Values, resources, seen);
        AddResources(_instruments.Values, resources, seen);

        return resources;
    }

    private static void AddResources(
        IEnumerable<object> source,
        ICollection<object> target,
        ISet<object> seen)
    {
        foreach (var resource in source)
        {
            if (seen.Add(resource))
            {
                target.Add(resource);
            }
        }
    }
}
