namespace TestFramework.Core.Resources;

/// <summary>
/// Hands out exclusive use of something by name. In-process for a resource only this process
/// opens (<see cref="ResourceLeaseTable"/>); across processes for a physical instrument several
/// stations' processes each open a session to (<see cref="CrossProcessLeaseProvider"/>).
/// </summary>
public interface IResourceLeaseProvider
{
    /// <summary>Waits for exclusive use of <paramref name="name"/>; dispose the result to give it up.</summary>
    Task<IDisposable> LeaseAsync(string name, CancellationToken cancellationToken);
}
