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
