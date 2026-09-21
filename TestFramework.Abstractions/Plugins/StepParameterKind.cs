namespace TestFramework.Abstractions.Plugins;

/// <summary>
/// The value kinds a step parameter can declare.
///
/// Deliberately not CLR types. The set is small enough for a host to render an editor for every
/// member and for a validator to check a YAML value against it without reflection, and it is the
/// same set that survives a crossing to another process - which is what a plugin worker process
/// would need. A plugin whose parameter does not fit here keeps its own settings editor.
/// </summary>
public enum StepParameterKind
{
    String,
    Integer,
    Number,
    Boolean,
    /// <summary>One of <see cref="StepParameterDescriptor.Choices"/>, compared as a string.</summary>
    Enum
}
