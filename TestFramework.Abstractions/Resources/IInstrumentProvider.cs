namespace TestFramework.Abstractions.Resources;

public interface IInstrumentProvider
{
    T GetRequired<T>(string id) where T : class;

    bool TryGet<T>(string id, out T instrument) where T : class;
}
