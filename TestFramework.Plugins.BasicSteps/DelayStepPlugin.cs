using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Plugins;
using TestFramework.Plugins.BasicSteps.Infrastructure;
using TestFramework.Plugins.BasicSteps.Settings;

namespace TestFramework.Plugins.BasicSteps;

public sealed class DelayStepPlugin : ITestStepPlugin
{
    public TestStepPluginDescriptor Descriptor { get; } = new()
    {
        PluginId = "basic.delay",
        DisplayName = "延时",
        Version = new Version(1, 0, 0),
        Category = "基础",
        Description = "等待指定的毫秒数。"
    };

    public Type SettingsType => typeof(DelayStepSettings);

    public object CreateDefaultSettings() => new DelayStepSettings();

    public object LoadSettings(IReadOnlyDictionary<string, object?> parameters)
    {
        return new DelayStepSettings
        {
            DelayMs = SettingsMap.GetInt(parameters, nameof(DelayStepSettings.DelayMs), 500)
        };
    }

    public IReadOnlyDictionary<string, object?> SaveSettings(object settings)
    {
        var typed = (DelayStepSettings)settings;
        return new Dictionary<string, object?> { [nameof(DelayStepSettings.DelayMs)] = typed.DelayMs };
    }

    public async Task<TestStepResult> ExecuteAsync(TestStepExecutionContext context, object settings, CancellationToken cancellationToken)
    {
        var typed = (DelayStepSettings)settings;
        var startedAt = DateTimeOffset.Now;
        await Task.Delay(Math.Max(0, typed.DelayMs), cancellationToken).ConfigureAwait(false);
        return new TestStepResult
        {
            Verdict = TestVerdict.Pass,
            StartedAt = startedAt,
            FinishedAt = DateTimeOffset.Now,
            Outputs = { ["delayMs"] = typed.DelayMs }
        };
    }

}
