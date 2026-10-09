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
    Enum,

    /// <summary>
    /// A <c>SequenceExpression</c> the plugin evaluates itself, such as <c>max(${cells}) - min(${cells})</c>.
    ///
    /// The runner does not substitute variables into it: the <c>${}</c> are the expression's
    /// operands, and replacing a list with its text would destroy them. The plugin receives the
    /// text and evaluates it against <c>TestStepExecutionContext.Variables</c>; the validator
    /// checks that it parses and that its variables exist. Added in framework contract 1.1 - a
    /// host built before it renders such a parameter as plain text.
    /// </summary>
    Expression
}
