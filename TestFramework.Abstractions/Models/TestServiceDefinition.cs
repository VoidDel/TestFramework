namespace TestFramework.Abstractions.Models;

public sealed class TestServiceDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string ServiceId { get; set; } = string.Empty;

    public string ServiceVersion { get; set; } = "1.0.0";

    public string? Transport { get; set; }

    public Dictionary<string, object?> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
