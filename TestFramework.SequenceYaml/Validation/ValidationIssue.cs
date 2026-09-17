namespace TestFramework.SequenceYaml.Validation;

/// <summary>
/// How much an issue matters. Errors mean the sequence cannot be saved or run; warnings are
/// reported to the operator but do not block, so that something merely worth knowing - a plugin
/// version that will be substituted, say - never becomes a reason the run is refused.
/// </summary>
public enum ValidationSeverity
{
    Error,
    Warning
}

public sealed class ValidationIssue
{
    public required string Path { get; init; }

    public required string Message { get; init; }

    public ValidationSeverity Severity { get; init; } = ValidationSeverity.Error;

    public bool IsError => Severity == ValidationSeverity.Error;

    public override string ToString()
    {
        var prefix = Severity == ValidationSeverity.Warning ? "warning" : "error";
        return $"{prefix} {Path}: {Message}";
    }
}
