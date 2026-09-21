namespace TestFramework.Abstractions.Plugins;

/// <summary>One allowed value of an <see cref="StepParameterKind.Enum"/> parameter.</summary>
public sealed class StepParameterChoice
{
    public StepParameterChoice(string value, string? displayName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
        DisplayName = displayName;
    }

    /// <summary>What is written to the sequence file.</summary>
    public string Value { get; }

    /// <summary>What an operator sees; falls back to <see cref="Value"/>.</summary>
    public string? DisplayName { get; }

    public override string ToString() => DisplayName ?? Value;
}
