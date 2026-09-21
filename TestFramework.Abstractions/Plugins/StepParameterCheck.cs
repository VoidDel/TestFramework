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
    public static IReadOnlyList<StepParameterProblem> Check(
        IReadOnlyList<StepParameterDescriptor> declared,
        IReadOnlyDictionary<string, object?> parameters,
        IReadOnlyCollection<string>? definedVariables = null)
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
        var variables = definedVariables is null
            ? null
            : new HashSet<string>(definedVariables, StringComparer.OrdinalIgnoreCase);

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

    private static void CheckOne(
        StepParameterDescriptor descriptor,
        IReadOnlyDictionary<string, object?> parameters,
        HashSet<string>? variables,
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
        HashSet<string>? variables,
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

        foreach (var name in VariableReference.NamesIn(value).Where(name => !variables.Contains(name)))
        {
            problems.Add(Problem(
                descriptor,
                $"Parameter '{descriptor.Label}' references variable '{name}', which is not defined."));
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
