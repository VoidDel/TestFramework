namespace TestFramework.Abstractions.Plugins;

/// <summary>
/// The outcome of resolving a plugin reference from a sequence file: which plugin instance will
/// run, and how its version relates to the one the sequence asked for.
/// </summary>
public readonly record struct PluginResolution<TPlugin>(
    TPlugin Plugin,
    PluginVersionMatch Match,
    string? RequestedVersion,
    Version ResolvedVersion)
{
    /// <summary>
    /// True when the sequence named a version that is not installed and a compatible newer one was
    /// substituted. Hosts surface this so the run record shows which code actually executed.
    /// </summary>
    public bool IsSubstituted => Match == PluginVersionMatch.Compatible;
}
