namespace TestFramework.Abstractions.Plugins;

/// <summary>
/// What one parameter of a step plugin is: its name, type, default, range and meaning.
///
/// Without this the framework knows a step's parameters only as a dictionary of <c>object?</c>, and
/// the knowledge of what belongs in it lives exclusively in the plugin's own private code. That
/// costs twice: a sequence can be validated, saved and reviewed with <c>DelayMs: abc</c> in it and
/// only fail part-way through a run, and every plugin that wants a usable editor has to hand-write
/// a form. Declaring the shape once lets the validator check values before a run starts and lets a
/// host build the editor, leaving custom editors for the parameters that genuinely need one.
///
/// It describes a value, not a control. A host is free to render a <see cref="StepParameterKind.Number"/>
/// with limits as a slider or a text box; nothing here tells it which.
/// </summary>
public sealed class StepParameterDescriptor
{
    /// <summary>
    /// The key in <see cref="Models.TestStepDefinition.Parameters"/>. Matched case-insensitively,
    /// like the dictionary itself.
    /// </summary>
    public required string Name { get; init; }

    public required StepParameterKind Kind { get; init; }

    /// <summary>What an operator sees; falls back to <see cref="Name"/>.</summary>
    public string? DisplayName { get; init; }

    /// <summary>One line explaining what the parameter does, shown beside the field.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// The value used when the sequence omits the parameter. It must match <see cref="Kind"/>, and
    /// it is what a host writes into a newly added step.
    /// </summary>
    public object? DefaultValue { get; init; }

    /// <summary>
    /// True when the sequence must carry a value. A required parameter with a default is a
    /// contradiction the validator reports rather than silently resolving.
    /// </summary>
    public bool IsRequired { get; init; }

    /// <summary>
    /// Inclusive bounds for <see cref="StepParameterKind.Integer"/> and
    /// <see cref="StepParameterKind.Number"/>; ignored for the other kinds.
    /// </summary>
    public double? Minimum { get; init; }

    public double? Maximum { get; init; }

    /// <summary>The allowed values of an <see cref="StepParameterKind.Enum"/> parameter.</summary>
    public IReadOnlyList<StepParameterChoice> Choices { get; init; } = [];

    /// <summary>
    /// Whether a <c>${variable}</c> reference may stand in for the value. True by default, because
    /// a parameter fed from a variable is the normal case in a production sequence; a plugin sets it
    /// false for a parameter that has to be known before the run starts.
    ///
    /// The referenced variable's value is only known at run time, so a reference suspends the type
    /// and range checks for that parameter - the validator checks that the variable exists instead.
    /// </summary>
    public bool AllowVariableReference { get; init; } = true;

    public string Label => DisplayName ?? Name;
}
