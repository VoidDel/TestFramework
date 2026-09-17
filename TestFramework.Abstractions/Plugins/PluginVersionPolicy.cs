namespace TestFramework.Abstractions.Plugins;

/// <summary>How a requested plugin version was satisfied.</summary>
public enum PluginVersionMatch
{
    /// <summary>No version was requested; the highest installed version was selected.</summary>
    Unspecified,

    /// <summary>The requested version is installed and was selected.</summary>
    Exact,

    /// <summary>
    /// The requested version is not installed; a higher version sharing its major version was
    /// selected instead. Callers should surface this, because the sequence no longer names the
    /// code that actually runs.
    /// </summary>
    Compatible
}

/// <summary>
/// The single rule for turning a version written in a sequence file into an installed plugin.
///
/// Exact matches win, so a sequence that names an installed version keeps running precisely that
/// code. When the named version is gone, a higher version with the same major version is accepted:
/// under semantic versioning that is the range promising backward compatibility. A different major
/// version, or only older versions, is never substituted - both can change behaviour the sequence
/// depends on, and silently measuring the wrong thing is worse than refusing to run.
/// </summary>
public static class PluginVersionPolicy
{
    public static bool TrySelect(
        IEnumerable<Version> installed,
        string? requestedVersion,
        out Version selected,
        out PluginVersionMatch match)
    {
        selected = null!;
        match = PluginVersionMatch.Unspecified;

        var candidates = installed as IReadOnlyCollection<Version> ?? installed.ToArray();
        if (candidates.Count == 0)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(requestedVersion))
        {
            selected = candidates.Max()!;
            return true;
        }

        if (!Version.TryParse(requestedVersion, out var requested))
        {
            return false;
        }

        if (candidates.Contains(requested))
        {
            selected = requested;
            match = PluginVersionMatch.Exact;
            return true;
        }

        var compatible = candidates
            .Where(candidate => candidate.Major == requested.Major && candidate > requested)
            .DefaultIfEmpty(null)
            .Max();

        if (compatible is null)
        {
            return false;
        }

        selected = compatible;
        match = PluginVersionMatch.Compatible;
        return true;
    }

    /// <summary>
    /// Describes the installed versions for an error message, so a version mismatch says what is
    /// actually available instead of only what is missing.
    /// </summary>
    public static string DescribeInstalled(IEnumerable<Version> installed)
    {
        var versions = installed.OrderByDescending(version => version).ToArray();
        return versions.Length == 0
            ? "none installed"
            : string.Join(", ", versions.Select(version => version.ToString()));
    }
}
