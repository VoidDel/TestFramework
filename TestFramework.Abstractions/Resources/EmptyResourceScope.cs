namespace TestFramework.Abstractions.Resources;

/// <summary>
/// A scope holding nothing, used as the default for contexts built without resources so callers
/// never have to null-check a provider.
/// </summary>
public sealed class EmptyResourceScope :
    IResourceScope,
    IInstrumentProvider,
    ITransportProvider,
    ITestServiceProvider
{
    public static EmptyResourceScope Instance { get; } = new();

    private EmptyResourceScope()
    {
    }

    public IInstrumentProvider Instruments => this;

    public ITransportProvider Transports => this;

    public ITestServiceProvider Services => this;

    T IInstrumentProvider.GetRequired<T>(string id) => throw Missing(id, "instrument", typeof(T));

    bool IInstrumentProvider.TryGet<T>(string id, out T instrument)
    {
        instrument = null!;
        return false;
    }

    T ITransportProvider.GetRequired<T>(string id) => throw Missing(id, "transport", typeof(T));

    bool ITransportProvider.TryGet<T>(string id, out T transport)
    {
        transport = null!;
        return false;
    }

    T ITestServiceProvider.GetRequired<T>(string id) => throw Missing(id, "service", typeof(T));

    bool ITestServiceProvider.TryGet<T>(string id, out T service)
    {
        service = null!;
        return false;
    }

    private static InvalidOperationException Missing(string id, string kind, Type type) =>
        new($"Runtime {kind} '{id}' of type '{type.Name}' is not available.");
}
