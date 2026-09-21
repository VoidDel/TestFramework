using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Plugins;
using TestFramework.Plugins.BasicSteps.Infrastructure;
using TestFramework.Plugins.BasicSteps.Settings;

namespace TestFramework.Plugins.BasicSteps;

public sealed class LimitCheckStepPlugin : ITestStepPlugin
{
    public TestStepPluginDescriptor Descriptor { get; } = new()
    {
        PluginId = "basic.limit-check",
        DisplayName = "限值检查",
        Version = new Version(1, 0, 0),
        Category = "基础",
        Description = "比较数值是否位于上下限范围内。"
    };

    public Type SettingsType => typeof(LimitCheckStepSettings);

    public IReadOnlyList<StepParameterDescriptor> Parameters { get; } =
    [
        new StepParameterDescriptor
        {
            Name = nameof(LimitCheckStepSettings.Value),
            Kind = StepParameterKind.Number,
            DisplayName = "测量值",
            Description = "参与比较的数值，通常来自变量。",
            DefaultValue = 5.0
        },
        new StepParameterDescriptor
        {
            Name = nameof(LimitCheckStepSettings.Min),
            Kind = StepParameterKind.Number,
            DisplayName = "下限",
            Description = "允许的最小值（含）。",
            DefaultValue = 4.8
        },
        new StepParameterDescriptor
        {
            Name = nameof(LimitCheckStepSettings.Max),
            Kind = StepParameterKind.Number,
            DisplayName = "上限",
            Description = "允许的最大值（含）。",
            DefaultValue = 5.2
        }
    ];

    public object CreateDefaultSettings() => new LimitCheckStepSettings();

    public object LoadSettings(IReadOnlyDictionary<string, object?> parameters)
    {
        return new LimitCheckStepSettings
        {
            Value = SettingsMap.GetDouble(parameters, nameof(LimitCheckStepSettings.Value), 5.0),
            Min = SettingsMap.GetDouble(parameters, nameof(LimitCheckStepSettings.Min), 4.8),
            Max = SettingsMap.GetDouble(parameters, nameof(LimitCheckStepSettings.Max), 5.2)
        };
    }

    public IReadOnlyDictionary<string, object?> SaveSettings(object settings)
    {
        var typed = (LimitCheckStepSettings)settings;
        return new Dictionary<string, object?>
        {
            [nameof(LimitCheckStepSettings.Value)] = typed.Value,
            [nameof(LimitCheckStepSettings.Min)] = typed.Min,
            [nameof(LimitCheckStepSettings.Max)] = typed.Max
        };
    }

    public Task<TestStepResult> ExecuteAsync(TestStepExecutionContext context, object settings, CancellationToken cancellationToken)
    {
        var typed = (LimitCheckStepSettings)settings;
        var startedAt = DateTimeOffset.Now;
        var passed = typed.Value >= typed.Min && typed.Value <= typed.Max;
        return Task.FromResult(new TestStepResult
        {
            Verdict = passed ? TestVerdict.Pass : TestVerdict.Fail,
            StartedAt = startedAt,
            FinishedAt = DateTimeOffset.Now,
            Outputs =
            {
                ["value"] = typed.Value,
                ["min"] = typed.Min,
                ["max"] = typed.Max,
                ["verdict"] = passed
            }
        });
    }

}
