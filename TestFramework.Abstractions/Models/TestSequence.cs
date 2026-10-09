namespace TestFramework.Abstractions.Models;

public sealed class TestSequence
{
    public int SchemaVersion { get; set; } = 1;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "New Test Sequence";

    /// <summary>
    /// The sequence's own revision, as its authors number it ("2.3", "B04"). Copied into every run
    /// result, so a result says which revision of the test produced it. Null when not numbered; the
    /// file hash a host records answers the same question exactly.
    /// </summary>
    public string? Version { get; set; }

    public Dictionary<string, object?> Variables { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The resources this sequence needs, by alias, for a station to bind to concrete hardware.
    ///
    /// This is the way forward: a sequence that declares requirements runs unchanged on every
    /// station that can satisfy them. <see cref="Instruments"/>, <see cref="Transports"/> and
    /// <see cref="Services"/> still carry addresses inline and still work, so existing sequences
    /// keep running - but a sequence written that way is pinned to one bench's wiring.
    /// </summary>
    public List<ResourceRequirement> Requires { get; set; } = [];

    public List<InstrumentDefinition> Instruments { get; set; } = [];

    public List<TransportDefinition> Transports { get; set; } = [];

    public List<TestServiceDefinition> Services { get; set; } = [];

    public List<TestItemDefinition> Items { get; set; } = [];
}
