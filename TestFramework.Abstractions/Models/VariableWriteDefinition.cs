namespace TestFramework.Abstractions.Models;

public sealed class VariableWriteDefinition
{
    public string Name { get; set; } = string.Empty;

    public string? OutputKey { get; set; }

    public object? Value { get; set; }

    public bool WriteOnError { get; set; }
}
