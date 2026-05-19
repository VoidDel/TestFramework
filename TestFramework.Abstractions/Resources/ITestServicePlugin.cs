using TestFramework.Abstractions.Models;

namespace TestFramework.Abstractions.Resources;

public interface ITestServicePlugin
{
    ResourcePluginDescriptor Descriptor { get; }

    Type ServiceType { get; }

    Task<object> CreateAsync(
        TestServiceDefinition definition,
        RuntimeResourceProvider resources,
        CancellationToken cancellationToken);
}
