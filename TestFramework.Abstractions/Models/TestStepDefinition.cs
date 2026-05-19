using System.Text.Json;

namespace TestFramework.Abstractions.Models;

public sealed class TestStepDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "New Step";

    public string PluginId { get; set; } = string.Empty;

    public string PluginVersion { get; set; } = "1.0.0";

    public bool Enabled { get; set; } = true;

    public int? TimeoutMs { get; set; }

    public ErrorHandlingMode OnError { get; set; } = ErrorHandlingMode.Stop;

    public Dictionary<string, object?> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<VariableWriteDefinition> VariableWrites { get; set; } = [];

    public TestStepDefinition Clone()
    {
        return new TestStepDefinition
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = Name + " Copy",
            PluginId = PluginId,
            PluginVersion = PluginVersion,
            Enabled = Enabled,
            TimeoutMs = TimeoutMs,
            OnError = OnError,
            Parameters = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                JsonSerializer.Serialize(Parameters)) ?? new Dictionary<string, object?>(),
            VariableWrites = JsonSerializer.Deserialize<List<VariableWriteDefinition>>(
                JsonSerializer.Serialize(VariableWrites)) ?? []
        };
    }
}
