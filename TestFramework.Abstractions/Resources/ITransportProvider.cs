namespace TestFramework.Abstractions.Resources;

public interface ITransportProvider
{
    T GetRequired<T>(string id) where T : class;

    bool TryGet<T>(string id, out T transport) where T : class;
}
