using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Plugins;
using TestFramework.Plugins.BasicSteps.Infrastructure;
using TestFramework.Plugins.BasicSteps.Settings;

namespace TestFramework.Plugins.BasicSteps;

public sealed class LogStepPlugin : ITestStepPlugin
{
    public TestStepPluginDescriptor Descriptor { get; } = new()
    {
        PluginId = "basic.log",
        DisplayName = "记录日志",
        Version = new Version(1, 0, 0),
        Category = "基础",
        Description = "向执行日志写入一条消息。"
    };

    public Type SettingsType => typeof(LogStepSettings);

    public object CreateDefaultSettings() => new LogStepSettings();

    public object LoadSettings(IReadOnlyDictionary<string, object?> parameters)
    {
        return new LogStepSettings
        {
            Message = SettingsMap.GetString(parameters, nameof(LogStepSettings.Message), "日志消息")
        };
    }

    public IReadOnlyDictionary<string, object?> SaveSettings(object settings)
    {
        var typed = (LogStepSettings)settings;
        return new Dictionary<string, object?> { [nameof(LogStepSettings.Message)] = typed.Message };
    }

    public Task<TestStepResult> ExecuteAsync(TestStepExecutionContext context, object settings, CancellationToken cancellationToken)
    {
        var typed = (LogStepSettings)settings;
        var startedAt = DateTimeOffset.Now;
        context.Log?.Invoke(typed.Message);
        return Task.FromResult(new TestStepResult
        {
            Verdict = TestVerdict.Pass,
            StartedAt = startedAt,
            FinishedAt = DateTimeOffset.Now,
            Outputs = { ["message"] = typed.Message }
        });
    }

}
