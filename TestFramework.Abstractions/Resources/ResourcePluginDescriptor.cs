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

    /// <summary>
    /// Units the instruments this plugin drives report in that the framework does not know - see
    /// <see cref="Plugins.TestStepPluginDescriptor.Units"/>. They join the table when the plugin is
    /// registered, not when a resource is created, which is after the sequence was validated.
    /// </summary>
    public IReadOnlyList<Models.MeasurementUnit> Units { get; init; } = [];
}
