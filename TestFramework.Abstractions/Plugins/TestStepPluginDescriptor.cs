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

    /// <summary>
    /// Units this plugin's outputs may carry that the framework does not know - a cell tester
    /// returning <c>"52.3 Ah"</c> needs nothing, one returning <c>"1.2 Nm"</c> declares
    /// <c>Nm</c> (contract 1.2). The registry adds them to <see cref="Models.MeasurementUnits"/> as it
    /// registers the plugin, so a sequence that judges them is validated against them; one that
    /// contradicts a unit already defined refuses the plugin.
    /// </summary>
    public IReadOnlyList<Models.MeasurementUnit> Units { get; init; } = [];
}
