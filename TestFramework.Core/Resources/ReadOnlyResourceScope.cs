using TestFramework.Abstractions.Resources;

namespace TestFramework.Core.Resources;

/// <summary>
/// The view of a <see cref="RuntimeResourceProvider"/> that plugin code receives.
///
/// The contract already exposes only lookups, but handing plugins the provider itself would let
/// a cast to <see cref="IDisposable"/> release every resource in the run. This wrapper implements
/// nothing but the lookup interfaces, so the container's lifetime stays with the host in fact as
/// well as in the type system. It is a guard against mistakes, not a sandbox: plugins run with
/// full host privileges regardless.
/// </summary>
public sealed class ReadOnlyResourceScope :
    IResourceScope,
    IInstrumentProvider,
    ITransportProvider,
    ITestServiceProvider
{
    private readonly IInstrumentProvider _instruments;
    private readonly ITransportProvider _transports;
    private readonly ITestServiceProvider _services;

    internal ReadOnlyResourceScope(RuntimeResourceProvider provider)
    {
        _instruments = provider;
        _transports = provider;
        _services = provider;
    }

    public IInstrumentProvider Instruments => this;

    public ITransportProvider Transports => this;

    public ITestServiceProvider Services => this;

    T IInstrumentProvider.GetRequired<T>(string id) => _instruments.GetRequired<T>(id);

    bool IInstrumentProvider.TryGet<T>(string id, out T instrument) => _instruments.TryGet(id, out instrument);

    T ITransportProvider.GetRequired<T>(string id) => _transports.GetRequired<T>(id);

    bool ITransportProvider.TryGet<T>(string id, out T transport) => _transports.TryGet(id, out transport);

    T ITestServiceProvider.GetRequired<T>(string id) => _services.GetRequired<T>(id);

    bool ITestServiceProvider.TryGet<T>(string id, out T service) => _services.TryGet(id, out service);
}
