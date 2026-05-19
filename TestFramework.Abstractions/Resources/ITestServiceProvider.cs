namespace TestFramework.Abstractions.Resources;

public interface ITestServiceProvider
{
    T GetRequired<T>(string id) where T : class;

    bool TryGet<T>(string id, out T service) where T : class;
}
