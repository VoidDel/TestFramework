using System.Reflection;

namespace TestFramework.Core.Execution;

/// <summary>The framework build a result was produced by, as a result records it.</summary>
public static class FrameworkBuild
{
    /// <summary>
    /// <c>TestFramework.Core</c>'s informational version without the source-revision suffix -
    /// "0.5.0", or "0.5.0-dev" for a local build. The package version, which is what a host pins.
    /// </summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var assembly = typeof(FrameworkBuild).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational))
        {
            return assembly.GetName().Version?.ToString() ?? "unknown";
        }

        var metadata = informational.IndexOf('+', StringComparison.Ordinal);
        return metadata < 0 ? informational : informational[..metadata];
    }
}
