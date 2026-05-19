namespace TestFramework.SequenceYaml.Validation;

public sealed class ValidationIssue
{
    public required string Path { get; init; }

    public required string Message { get; init; }

    public override string ToString()
    {
        return $"{Path}: {Message}";
    }
}
