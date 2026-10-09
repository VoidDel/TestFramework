using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Expressions;
using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Plugins;
using TestFramework.Plugins.BasicSteps.Infrastructure;
using TestFramework.Plugins.BasicSteps.Settings;

namespace TestFramework.Plugins.BasicSteps;

/// <summary>
/// Evaluates an expression - <c>max(${cells}) - min(${cells})</c>, <c>${charged} - ${before}</c> -
/// and puts the result in the output <c>value</c>.
///
/// It does not assign the variable itself. A step that wrote <c>context.Variables</c> directly
/// would define a variable the validator cannot see, and every later <c>${spread}</c> would be
/// reported as undefined; writing it through <c>variableWrites</c> is the one way a variable comes
/// into existence that the validator follows, so that is how the result is meant to be kept.
/// </summary>
public sealed class CalculateStepPlugin : ITestStepPlugin
{
    public TestStepPluginDescriptor Descriptor { get; } = new()
    {
        PluginId = "basic.calculate",
        DisplayName = "计算",
        Version = new Version(1, 0, 0),
        Category = "基础",
        Description = "计算表达式，结果放在输出 value 中，可用 variableWrites 写入变量。"
    };

    public Type SettingsType => typeof(CalculateStepSettings);

    public bool IsThreadSafe => true;

    public IReadOnlyList<StepParameterDescriptor> Parameters { get; } =
    [
        new StepParameterDescriptor
        {
            Name = nameof(CalculateStepSettings.Expression),
            Kind = StepParameterKind.Expression,
            DisplayName = "表达式",
            Description = "例如 max(${cells}) - min(${cells})；变量写作 ${名称}。",
            IsRequired = true
        }
    ];

    public object CreateDefaultSettings() => new CalculateStepSettings();

    public object LoadSettings(IReadOnlyDictionary<string, object?> parameters)
    {
        return new CalculateStepSettings
        {
            Expression = SettingsMap.GetString(parameters, nameof(CalculateStepSettings.Expression), string.Empty)
        };
    }

    public IReadOnlyDictionary<string, object?> SaveSettings(object settings)
    {
        var typed = (CalculateStepSettings)settings;
        return new Dictionary<string, object?> { [nameof(CalculateStepSettings.Expression)] = typed.Expression };
    }

    public Task<TestStepResult> ExecuteAsync(TestStepExecutionContext context, object settings, CancellationToken cancellationToken)
    {
        var typed = (CalculateStepSettings)settings;
        var startedAt = DateTimeOffset.Now;

        // A syntax error or an undefined variable throws, which the runner records as this step's
        // Error - a computed limit that silently came out as nothing must not be judged against.
        var value = SequenceExpression.Parse(typed.Expression).Evaluate(context.Variables);
        context.Log?.Invoke($"{typed.Expression} = {Describe(value)}");
        return Task.FromResult(new TestStepResult
        {
            Verdict = TestVerdict.Pass,
            StartedAt = startedAt,
            FinishedAt = DateTimeOffset.Now,
            Outputs = { ["value"] = value }
        });
    }

    private static string Describe(object? value) => value switch
    {
        null => "null",
        IReadOnlyCollection<object?> list => $"[{list.Count} values]",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
    };
}
