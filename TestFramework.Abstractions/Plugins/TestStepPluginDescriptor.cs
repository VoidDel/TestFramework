namespace TestFramework.Abstractions.Plugins;

public sealed class TestStepPluginDescriptor
{
    public required string PluginId { get; init; }

    public required string DisplayName { get; init; }

    public required Version Version { get; init; }

    /// <summary>
    /// Where this step belongs in a host's step palette, with <c>/</c> separating levels - "基础" or
    /// "电池/充电". Null means the plugin states no opinion, and a host is free to group it by
    /// whatever it knows instead, typically the folder the plugin was deployed in.
    ///
    /// The distinction matters: a literal default like "General" is indistinguishable from an author
    /// who chose that name, so a host could never tell an undeclared category from a declared one.
    /// </summary>
    public string? Category { get; init; }

    public string? Description { get; init; }
}
