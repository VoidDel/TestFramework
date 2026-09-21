namespace TestFramework.Core.Resources;

/// <summary>
/// Releases a half-built resource scope after the build failed.
///
/// One implementation, because three places need it and they have to agree. A scope that failed
/// part-way through is still holding everything it managed to open, so it has to be released - and
/// releasing it can fail too: a bench whose meter is missing is exactly the bench whose supply is
/// also in a bad way. Letting that second failure propagate on its own replaces the reason the
/// build failed, and the operator is sent after "the supply would not close" when the actual fault
/// was "the meter is not on the bench". Both have to survive, and the build error has to come
/// first, because it is the one that says why nothing opened.
/// </summary>
internal static class ResourceScopeCleanup
{
    /// <summary>
    /// Disposes <paramref name="scope"/> after <paramref name="buildError"/> interrupted the build.
    /// Returns normally when the release succeeded, leaving the caller to rethrow the build error;
    /// throws an <see cref="AggregateException"/> carrying both when the release failed as well.
    /// </summary>
    public static async Task DisposeAfterFailureAsync(RuntimeResourceProvider scope, Exception buildError)
    {
        try
        {
            await scope.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception cleanupError)
        {
            throw new AggregateException("Resource initialization and cleanup failed.", buildError, cleanupError);
        }
    }
}
