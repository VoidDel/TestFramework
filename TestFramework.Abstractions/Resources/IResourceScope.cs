namespace TestFramework.Abstractions.Resources;

/// <summary>
/// The read-only view of a run's resources handed to resource plugins while they build.
///
/// A driver often needs a resource that was opened before it - a service reaching for its
/// transport, say - but it must not register into, or dispose, the host's container: the container
/// is shared by every resource in the run, and the host owns its lifetime. Exposing only lookups
/// keeps that ownership in the type system instead of in a convention plugin authors have to know.
/// </summary>
public interface IResourceScope
{
    IInstrumentProvider Instruments { get; }

    ITransportProvider Transports { get; }

    ITestServiceProvider Services { get; }
}
