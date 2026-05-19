namespace TestFramework.Abstractions.Resources;

public sealed class ResourcePluginDescriptor
{
    public required string PluginId { get; init; }

    public required string DisplayName { get; init; }

    public required Version Version { get; init; }

    public string Category { get; init; } = "General";

    public string? Description { get; init; }
}
