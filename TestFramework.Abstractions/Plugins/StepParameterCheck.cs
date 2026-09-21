using System.Globalization;
using TestFramework.Abstractions.Models;

namespace TestFramework.Abstractions.Plugins;

/// <summary>What is wrong with one parameter value.</summary>
public sealed class StepParameterProblem
{
    public required string ParameterName { get; init; }

    public required string Message { get; init; }

    /// <summary>
    /// True for something the operator should see but which does not stop a run - a parameter the
    /// plugin does not declare, say, which is usually a typo but may be a key a newer build reads.
    /// </summary>
    public bool IsWarning { get; init; }
}

/// <summary>
/// Checks a step's parameter values against what its plugin declares.
///
/// It lives beside the contract rather than in the YAML layer because two callers need the same
/// answers: the sequence validator, which runs before a sequence is saved or started, and an
/// editor, which tells the operator about a bad value while they are typing it. Two
/// implementations would eventually disagree, and the one that matters - the validator - is the
/// one the operator would not be looking at.
///
/// It checks values, not variables' contents: a <c>${name}</c> reference resolves at run time, so
/// the type and range of what it carries cannot be known here. What can be known, and is checked,
/// is that the variable exists.
/// </summary>
public static class StepParameterCheck
{
    /// <summary>
    /// Checks a step's parameter values against what its plugin declares.
    /// </summary>
    /// <param name="definedVariables">
    /// The variables that exist where this step runs, mapped to the value each is statically known
    /// to hold - or <c>null</c> where it is not knowable, which is the case for anything a plugin
    /// writes. A key must be present for the variable to count as defined, so a caller that knows
    /// only the names maps them all to <c>null</c>. Passing the whole map instead of just the names
    /// is what lets <c>${targetVoltage}</c> in a Number parameter be checked at all, rather than
    /// suspending every check the moment a value comes from a variable.
    /// </param>
    public static IReadOnlyList<StepParameterProblem> Check(
        IReadOnlyList<StepParameterDescriptor> declared,
        IReadOnlyDictionary<string, object?> parameters,
        IReadOnlyDictionary<string, object?>? definedVariables = null)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(parameters);

        // A plugin that declares nothing keeps the behaviour it had before parameters could be
        // declared at all: the framework has no opinion about its dictionary.
        if (declared.Count == 0)
        {
            return [];
        }

        var problems = new List<StepParameterProblem>();
        var variables = CaseInsensitive(definedVariables);

        foreach (var descriptor in declared)
        {
            CheckOne(descriptor, parameters, variables, problems);
        }

        var known = declared.Select(descriptor => descriptor.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in parameters.Keys.Where(key => !known.Contains(key)))
        {
            problems.Add(new StepParameterProblem
            {
                ParameterName = key,
                IsWarning = true,
                Message = $"Parameter '{key}' is not declared by this plugin and will be ignored."
            });
        }

        return problems;
    }

    /// <summary>
    /// A case-insensitive copy, because the variable table is case-insensitive and a caller may
    /// hand over any dictionary. Built with the indexer rather than a copy constructor: two keys
    /// differing only by case must not turn a check into an exception.
    /// </summary>
    private static Dictionary<string, object?>? CaseInsensitive(IReadOnlyDictionary<string, object?>? source)
    {
        if (source is null)
        {
            return null;
        }

        var copy = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in source)
        {
            copy[key] = value;
        }

        return copy;
    }

    private static void CheckOne(
        StepParameterDescriptor descriptor,
        IReadOnlyDictionary<string, object?> parameters,
        Dictionary<string, object?>? variables,
        ICollection<StepParameterProblem> problems)
    {
        if (!parameters.TryGetValue(descriptor.Name, out var value) || value is null)
        {
            // Absent is only a problem when there is no default to fall back on.
            if (descriptor.IsRequired && descriptor.DefaultValue is null)
            {
                problems.Add(Problem(descriptor, $"Parameter '{descriptor.Label}' is required."));
            }

            return;
        }

        if (VariableReference.IsReference(value))
        {
            CheckVariableReference(descriptor, value, variables, problems);
            return;
        }

        // Looks like a reference but is not one: ${1stReading}, ${my var}, a missing closing brace.
        // The resolver leaves it alone and the plugin receives the literal text, so without this
        // nothing downstream ever objects - and a near-miss is exactly the shape a mistyped variable
        // name takes. A warning rather than an error, because a parameter is allowed to carry '${'
        // on purpose - it writes '$${' to say so, which is why this can tell the two apart.
        if (VariableReference.HasMalformedReference(value))
        {
            problems.Add(new StepParameterProblem
            {
                ParameterName = descriptor.Name,
                IsWarning = true,
                Message = $"Parameter '{descriptor.Label}' contains '${{' but no valid variable reference, so it is passed through as literal text. A variable name starts with a letter or underscore and continues with letters, digits, '_', '.' or '-'."
            });
        }

        switch (descriptor.Kind)
        {
            case StepParameterKind.Integer:
                CheckInteger(descriptor, value, problems);
                break;
            case StepParameterKind.Number:
                CheckNumber(descriptor, value, problems);
                break;
            case StepParameterKind.Boolean:
                if (!TryAsBoolean(value, out _))
                {
                    problems.Add(Problem(descriptor, $"Parameter '{descriptor.Label}' must be true or false, but is '{Describe(value)}'."));
                }

                break;
            case StepParameterKind.Enum:
                CheckEnum(descriptor, value, problems);
                break;
            case StepParameterKind.String:
                break;
        }
    }

    private static void CheckVariableReference(
        StepParameterDescriptor descriptor,
        object? value,
        Dictionary<string, object?>? variables,
        ICollection<StepParameterProblem> problems)
    {
        if (!descriptor.AllowVariableReference)
        {
            problems.Add(Problem(
                descriptor,
                $"Parameter '{descriptor.Label}' must be a literal value; it cannot reference a variable."));
            return;
        }

        if (variables is null)
        {
            return;
        }

        foreach (var name in VariableReference.NamesIn(value))
        {
            if (!variables.TryGetValue(name, out var known))
            {
                problems.Add(Problem(
                    descriptor,
                    $"Parameter '{descriptor.Label}' references variable '{name}', which is not defined."));
                continue;
            }

            // The type is only checkable when the reference is the whole value - embedded in text
            // the result is text whatever the variable holds - and when the variable's value is
            // actually knowable before the run. A null here means it is not: something writes it
            // and only the plugin decides what comes out.
            if (known is not null && VariableReference.IsWholeValueReference(value))
            {
                CheckKnownVariableValue(descriptor, name, known, problems);
            }
        }
    }

    /// <summary>
    /// Checks what a variable is known to hold against the kind the parameter declares.
    ///
    /// This is the one thing declaring parameters was supposed to buy - catching a wrong type
    /// before a run rather than part-way through one with a DUT connected - and a reference used to
    /// switch it off entirely. For a variable whose value the file states and nothing reassigns,
    /// the type is as knowable as a literal's, so it is checked like one.
    ///
    /// The range is deliberately not checked, only the type: a limit belongs to the value a run
    /// produces, and the initial value is not that.
    /// </summary>
    private static void CheckKnownVariableValue(
        StepParameterDescriptor descriptor,
        string name,
        object known,
        ICollection<StepParameterProblem> problems)
    {
        var expectation = descriptor.Kind switch
        {
            StepParameterKind.Integer when !(TryAsDouble(known, out var number) && number == Math.Truncate(number)) =>
                "a whole number",
            StepParameterKind.Number when !TryAsDouble(known, out _) => "a number",
            StepParameterKind.Boolean when !TryAsBoolean(known, out _) => "true or false",
            StepParameterKind.Enum when descriptor.Choices.Count > 0 && !descriptor.Choices.Any(
                choice => string.Equals(choice.Value, Describe(known), StringComparison.OrdinalIgnoreCase)) =>
                "one of " + string.Join(", ", descriptor.Choices.Select(choice => $"'{choice.Value}'")),
            _ => null
        };

        if (expectation is not null)
        {
            problems.Add(Problem(
                descriptor,
                $"Parameter '{descriptor.Label}' must be {expectation}, but variable '{name}' holds '{Describe(known)}'."));
        }
    }

    private static void CheckInteger(
        StepParameterDescriptor descriptor,
        object value,
        ICollection<StepParameterProblem> problems)
    {
        if (!TryAsDouble(value, out var number) || number != Math.Truncate(number))
        {
            problems.Add(Problem(descriptor, $"Parameter '{descriptor.Label}' must be a whole number, but is '{Describe(value)}'."));
            return;
        }

        CheckRange(descriptor, number, problems);
    }

    private static void CheckNumber(
        StepParameterDescriptor descriptor,
        object value,
        ICollection<StepParameterProblem> problems)
    {
        if (!TryAsDouble(value, out var number))
        {
            problems.Add(Problem(descriptor, $"Parameter '{descriptor.Label}' must be a number, but is '{Describe(value)}'."));
            return;
        }

        CheckRange(descriptor, number, problems);
    }

    private static void CheckRange(
        StepParameterDescriptor descriptor,
        double number,
        ICollection<StepParameterProblem> problems)
    {
        if (descriptor.Minimum is { } minimum && number < minimum)
        {
            problems.Add(Problem(descriptor, $"Parameter '{descriptor.Label}' must be at least {Format(minimum)}, but is {Format(number)}."));
        }

        if (descriptor.Maximum is { } maximum && number > maximum)
        {
            problems.Add(Problem(descriptor, $"Parameter '{descriptor.Label}' must be at most {Format(maximum)}, but is {Format(number)}."));
        }
    }

    private static void CheckEnum(
        StepParameterDescriptor descriptor,
        object value,
        ICollection<StepParameterProblem> problems)
    {
        if (descriptor.Choices.Count == 0)
        {
            return;
        }

        var text = Describe(value);
        if (descriptor.Choices.Any(choice => string.Equals(choice.Value, text, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var allowed = string.Join(", ", descriptor.Choices.Select(choice => $"'{choice.Value}'"));
        problems.Add(Problem(descriptor, $"Parameter '{descriptor.Label}' must be one of {allowed}, but is '{text}'."));
    }

    /// <summary>
    /// Accepts the numeric shapes a YAML file and an editor actually produce. A value typed into a
    /// text box arrives as a string, and YAML gives back long or double depending on how the number
    /// was written; refusing those would report errors on sequences that run correctly.
    /// </summary>
    private static bool TryAsDouble(object value, out double number)
    {
        switch (value)
        {
            case double existing:
                number = existing;
                return double.IsFinite(existing);
            case float existing:
                number = existing;
                return float.IsFinite(existing);
            case int or long or short or byte or uint or ulong or ushort or sbyte:
                number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return true;
            case decimal existing:
                number = (double)existing;
                return true;
            case string text:
                return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                    && double.IsFinite(number);
            default:
                number = 0;
                return false;
        }
    }

    private static bool TryAsBoolean(object value, out bool result)
    {
        switch (value)
        {
            case bool existing:
                result = existing;
                return true;
            case string text:
                return bool.TryParse(text.Trim(), out result);
            default:
                result = false;
                return false;
        }
    }

    private static StepParameterProblem Problem(StepParameterDescriptor descriptor, string message) =>
        new() { ParameterName = descriptor.Name, Message = message };

    private static string Describe(object? value) =>
        Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Format(double value) =>
        value.ToString("0.############", CultureInfo.InvariantCulture);
}
