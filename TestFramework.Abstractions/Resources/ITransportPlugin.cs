using TestFramework.Abstractions.Models;

namespace TestFramework.Abstractions.Resources;

public interface ITransportPlugin
{
    ResourcePluginDescriptor Descriptor { get; }

    Type TransportType { get; }

    Task<object> CreateAsync(
        TransportDefinition definition,
        IResourceScope resources,
        CancellationToken cancellationToken);
}
