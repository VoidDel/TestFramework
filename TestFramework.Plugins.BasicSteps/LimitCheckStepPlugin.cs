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
