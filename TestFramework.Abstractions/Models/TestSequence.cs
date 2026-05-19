namespace TestFramework.Abstractions.Models;

public sealed class TestSequence
{
    public int SchemaVersion { get; set; } = 1;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "New Test Sequence";

    public Dictionary<string, object?> Variables { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<InstrumentDefinition> Instruments { get; set; } = [];

    public List<TransportDefinition> Transports { get; set; } = [];

    public List<TestServiceDefinition> Services { get; set; } = [];

    public List<TestItemDefinition> Items { get; set; } = [];
}
