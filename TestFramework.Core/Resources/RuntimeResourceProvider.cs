namespace TestFramework.Abstractions.Resources;

public sealed class RuntimeResourceProvider :
    IInstrumentProvider,
    ITransportProvider,
    ITestServiceProvider,
    IDisposable,
    IAsyncDisposable
{
    public static RuntimeResourceProvider Empty { get; } = new(isReadOnly: true);

    private readonly bool _isReadOnly;
    private readonly Dictionary<string, object> _instruments = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, object> _transports = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, object> _services = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public RuntimeResourceProvider()
    {
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

    T IInstrumentProvider.GetRequired<T>(string id)
    {
        return GetRequired<T>(_instruments, id, "instrument");
    }

    bool IInstrumentProvider.TryGet<T>(string id, out T instrument)
    {
        return TryGet(_instruments, id, out instrument);
    }

    T ITransportProvider.GetRequired<T>(string id)
    {
        return GetRequired<T>(_transports, id, "transport");
    }

    bool ITransportProvider.TryGet<T>(string id, out T transport)
    {
        return TryGet(_transports, id, out transport);
    }

    T ITestServiceProvider.GetRequired<T>(string id)
    {
        return GetRequired<T>(_services, id, "service");
    }

    bool ITestServiceProvider.TryGet<T>(string id, out T service)
    {
        return TryGet(_services, id, out service);
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

    private static T GetRequired<T>(IReadOnlyDictionary<string, object> source, string id, string kind)
        where T : class
    {
        if (TryGet(source, id, out T value))
        {
            return value;
        }

        throw new InvalidOperationException($"Runtime {kind} '{id}' of type '{typeof(T).Name}' is not available.");
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
