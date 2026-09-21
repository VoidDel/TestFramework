namespace TestFramework.Abstractions.Resources;

public sealed class ResourcePluginDescriptor
{
    public required string PluginId { get; init; }

    public required string DisplayName { get; init; }

    public required Version Version { get; init; }

    /// <summary>
    /// Where this resource plugin belongs in a host's palette. Null means the plugin states no
    /// opinion - see <see cref="Plugins.TestStepPluginDescriptor.Category"/>.
    /// </summary>
    public string? Category { get; init; }

    public string? Description { get; init; }
}
