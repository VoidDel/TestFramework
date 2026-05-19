namespace TestFramework.Abstractions.Models;

public sealed class InstrumentDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string DriverId { get; set; } = string.Empty;

    public string DriverVersion { get; set; } = "1.0.0";

    public string Resource { get; set; } = string.Empty;

    public Dictionary<string, object?> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
