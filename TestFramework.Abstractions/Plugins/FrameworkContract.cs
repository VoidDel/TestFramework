namespace TestFramework.Abstractions.Plugins;

/// <summary>
/// The version of the plugin-facing contract this framework build exposes.
///
/// It is deliberately separate from the package version: packages ship on their own cadence, while
/// this number moves only when the surface plugins compile against changes. A plugin declares the
/// lowest contract it needs through <see cref="TestFrameworkPluginAttribute"/>, and a host accepts
/// it when that number is at or below <see cref="Version"/>.
///
/// The contract is additive-only, so a newer framework keeps running older plugins. In practice
/// that means every member added to an existing plugin-facing interface carries a default
/// implementation, and existing members never change signature or meaning. A change that cannot be
/// made that way is a new major contract version, and the old plugins it locks out are refused at
/// load time with a message rather than failing somewhere inside a run.
/// </summary>
public static class FrameworkContract
{
    public static Version Version { get; } = new(1, 0);

    /// <summary>The contract assumed for a plugin assembly that declares none.</summary>
    public static Version Baseline { get; } = new(1, 0);

    /// <summary>
    /// True when this framework can host a plugin that requires <paramref name="minimumRequired"/>.
    /// </summary>
    public static bool Supports(Version minimumRequired)
    {
        ArgumentNullException.ThrowIfNull(minimumRequired);
        return Version >= minimumRequired;
    }
}
