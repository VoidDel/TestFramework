using TestFramework.Abstractions.Models;

namespace TestFramework.Abstractions.Resources;

public interface IInstrumentDriverPlugin
{
    ResourcePluginDescriptor Descriptor { get; }

    Type InstrumentType { get; }

    Task<object> CreateAsync(
        InstrumentDefinition definition,
        IResourceScope resources,
        CancellationToken cancellationToken);
}
