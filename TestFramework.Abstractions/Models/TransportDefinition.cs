namespace TestFramework.Abstractions.Models;

public sealed class TransportDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string TransportId { get; set; } = string.Empty;

    public string TransportVersion { get; set; } = "1.0.0";

    public string? Channel { get; set; }

    public Dictionary<string, object?> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
