using System.Collections;
using System.Globalization;
using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Plugins;

namespace TestFramework.Plugins.InstrumentSteps;

/// <summary>
/// What every step here shares: it names its resource by alias in <c>Resource</c>, asks for it by
/// the standard class only - never a vendor's type - and keeps its parameters as the dictionary the
/// sequence wrote, read with the types the declarations promise.
///
/// The steps hold nothing between calls, so they are thread-safe; what must not interleave is the
/// instrument, and that is the sequence's <c>exclusive</c> on the alias, as for any shared resource.
/// </summary>
public abstract class InstrumentStepPlugin : ITestStepPlugin
{
    public const string ResourceParameter = "Resource";

    protected InstrumentStepPlugin(string pluginId, string displayName, string category, string description)
    {
        Descriptor = new TestStepPluginDescriptor
        {
            PluginId = pluginId,
            DisplayName = displayName,
            Version = new Version(1, 0, 0),
            Category = category,
            Description = description
        };
    }

    public TestStepPluginDescriptor Descriptor { get; }

    public Type SettingsType => typeof(StepSettings);

    public bool IsThreadSafe => true;

    public abstract IReadOnlyList<StepParameterDescriptor> Parameters { get; }

    public object CreateDefaultSettings() =>
        new StepSettings(Parameters.Where(parameter => parameter.DefaultValue is not null)
            .ToDictionary(parameter => parameter.Name, parameter => parameter.DefaultValue, StringComparer.OrdinalIgnoreCase));

    public object LoadSettings(IReadOnlyDictionary<string, object?> parameters) => new StepSettings(parameters);

    public IReadOnlyDictionary<string, object?> SaveSettings(object settings) => ((StepSettings)settings).Values;

    public async Task<TestStepResult> ExecuteAsync(TestStepExecutionContext context, object settings, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.Now;
        var result = await RunAsync(context, (StepSettings)settings, cancellationToken).ConfigureAwait(false);
        result.StartedAt = startedAt;
        result.FinishedAt = DateTimeOffset.Now;
        return result;
    }

    protected abstract Task<TestStepResult> RunAsync(TestStepExecutionContext context, StepSettings settings, CancellationToken cancellationToken);

    /// <summary>The instrument the step's <c>Resource</c> names, as the class it needs.</summary>
    protected static T Instrument<T>(TestStepExecutionContext context, StepSettings settings, string className)
        where T : class
    {
        var alias = settings.Required(ResourceParameter);
        return context.Instruments.TryGet<T>(alias, out var instrument)
            ? instrument
            : throw new InvalidOperationException(
                $"Instrument '{alias}' is not in this run's resources as a {className}. Bind it in the station file, and declare it in requires with class: {className}.");
    }

    /// <summary>The transport the step's <c>Resource</c> names, as the class it needs.</summary>
    protected static T Transport<T>(TestStepExecutionContext context, StepSettings settings, string className)
        where T : class
    {
        var alias = settings.Required(ResourceParameter);
        return context.Transports.TryGet<T>(alias, out var transport)
            ? transport
            : throw new InvalidOperationException(
                $"Transport '{alias}' is not in this run's resources as a {className}. Bind it in the station file with kind: Transport, and declare it in requires with class: {className}.");
    }

    protected static StepParameterDescriptor Resource(string className, string what) => new()
    {
        Name = ResourceParameter,
        Kind = StepParameterKind.String,
        DisplayName = "资源",
        Description = $"{what}的别名，即序列 requires 里 class: {className} 的那一项。",
        IsRequired = true
    };

    protected static StepParameterDescriptor Number(string name, string displayName, string description, double? minimum = null, double? defaultValue = null) => new()
    {
        Name = name,
        Kind = StepParameterKind.Number,
        DisplayName = displayName,
        Description = description,
        Minimum = minimum,
        DefaultValue = defaultValue
    };

    protected static StepParameterDescriptor Text(string name, string displayName, string description, bool required = false) => new()
    {
        Name = name,
        Kind = StepParameterKind.String,
        DisplayName = displayName,
        Description = description,
        IsRequired = required
    };

    protected static StepParameterDescriptor Choice<TEnum>(string name, string displayName, string description, TEnum? defaultValue = null)
        where TEnum : struct, Enum => new()
    {
        Name = name,
        Kind = StepParameterKind.Enum,
        DisplayName = displayName,
        Description = description,
        DefaultValue = defaultValue?.ToString(),
        Choices = Enum.GetNames<TEnum>().Select(value => new StepParameterChoice(value)).ToArray()
    };

    /// <summary>On, off, or leave as it is - the last is the default, so a step that only sets a level does not switch anything.</summary>
    protected static StepParameterDescriptor Switch(string name, string displayName, string description) => new()
    {
        Name = name,
        Kind = StepParameterKind.Enum,
        DisplayName = displayName,
        Description = description,
        DefaultValue = nameof(SwitchAction.Unchanged),
        Choices = [new StepParameterChoice(nameof(SwitchAction.Unchanged), "不变"), new StepParameterChoice(nameof(SwitchAction.On), "打开"), new StepParameterChoice(nameof(SwitchAction.Off), "关闭")]
    };

    protected internal static TestStepResult Pass(params (string Key, object? Value)[] outputs)
    {
        var result = new TestStepResult { Verdict = TestVerdict.Pass };
        foreach (var (key, value) in outputs)
        {
            result.Outputs[key] = value;
        }

        return result;
    }

    protected internal static TestStepResult Fail(string message, params (string Key, object? Value)[] outputs)
    {
        var result = Pass(outputs);
        result.Verdict = TestVerdict.Fail;
        result.ErrorMessage = message;
        return result;
    }
}

public enum SwitchAction
{
    Unchanged,
    On,
    Off
}

/// <summary>
/// A step's parameters as the sequence wrote them, read with the type each declaration promises.
/// A value the validator would have refused - it can still arrive through a variable - is an error
/// naming the parameter, so the step ends <c>Error</c> with a reason rather than with a cast.
/// </summary>
public sealed class StepSettings(IReadOnlyDictionary<string, object?> values)
{
    public IReadOnlyDictionary<string, object?> Values { get; } =
        new Dictionary<string, object?>(values, StringComparer.OrdinalIgnoreCase);

    public bool Has(string name) => Values.TryGetValue(name, out var value) && value is not null && value is not string { Length: 0 };

    public string Required(string name) =>
        Has(name) ? Text(name)! : throw new ArgumentException($"Parameter '{name}' is required.");

    public string? Text(string name) =>
        Has(name) ? Convert.ToString(Values[name], CultureInfo.InvariantCulture)?.Trim() : null;

    public double? Number(string name)
    {
        if (!Has(name))
        {
            return null;
        }

        var value = Values[name];
        var number = value switch
        {
            double d => d,
            float or int or long or decimal => Convert.ToDouble(value, CultureInfo.InvariantCulture),
            string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => throw new ArgumentException($"Parameter '{name}' must be a number, not '{value}'.")
        };
        return double.IsFinite(number) ? number : throw new ArgumentException($"Parameter '{name}' must be finite.");
    }

    public double Number(string name, double fallback) => Number(name) ?? fallback;

    public double RequiredNumber(string name) => Number(name) ?? throw new ArgumentException($"Parameter '{name}' is required.");

    public bool Flag(string name, bool fallback)
    {
        if (!Has(name))
        {
            return fallback;
        }

        return Values[name] switch
        {
            bool flag => flag,
            string text when bool.TryParse(text, out var parsed) => parsed,
            var other => throw new ArgumentException($"Parameter '{name}' must be true or false, not '{other}'.")
        };
    }

    public TEnum Choice<TEnum>(string name, TEnum fallback)
        where TEnum : struct, Enum
    {
        var text = Text(name);
        if (text is null)
        {
            return fallback;
        }

        return Enum.TryParse<TEnum>(text, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new ArgumentException($"Parameter '{name}' must be one of {string.Join(", ", Enum.GetNames<TEnum>())}, not '{text}'.");
    }

    /// <summary>
    /// A list the sequence wrote either as a YAML list or as text: "101,102" or "101-108", a range
    /// whose ends share a prefix and whose numbers keep their width ("ai00-ai03").
    /// </summary>
    public IReadOnlyList<string> List(string name)
    {
        if (!Has(name))
        {
            return [];
        }

        var items = Values[name] is IEnumerable sequence and not string
            ? sequence.Cast<object?>().Select(item => Convert.ToString(item, CultureInfo.InvariantCulture) ?? string.Empty)
            : (Text(name) ?? string.Empty).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return items.SelectMany(item => ExpandRange(name, item.Trim())).ToArray();
    }

    private static IEnumerable<string> ExpandRange(string name, string item)
    {
        var dash = item.IndexOf('-', 1);
        if (dash < 0)
        {
            return [item];
        }

        var (first, last) = (item[..dash].Trim(), item[(dash + 1)..].Trim());
        var (prefix, start, width) = Split(first);
        var (lastPrefix, end, _) = Split(last);
        if (start is null || end is null || !string.Equals(prefix, lastPrefix, StringComparison.Ordinal) || end < start || end - start > 10_000)
        {
            throw new ArgumentException($"Parameter '{name}': '{item}' is not a range such as 101-108.");
        }

        return Enumerable.Range(start.Value, end.Value - start.Value + 1)
            .Select(number => prefix + number.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0'));
    }

    private static (string Prefix, int? Number, int Width) Split(string text)
    {
        var digits = text.Length - text.Reverse().TakeWhile(char.IsAsciiDigit).Count();
        return digits == text.Length
            ? (text, null, 0)
            : (text[..digits], int.Parse(text[digits..], CultureInfo.InvariantCulture), text.Length - digits);
    }
}
