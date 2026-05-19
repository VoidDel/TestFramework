using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Plugins;
using TestFramework.Plugins.BasicSteps.Infrastructure;
using TestFramework.Plugins.BasicSteps.Settings;

namespace TestFramework.Plugins.BasicSteps;

public sealed class ThrowStepPlugin : ITestStepPlugin
{
    public TestStepPluginDescriptor Descriptor { get; } = new()
    {
        PluginId = "basic.throw",
        DisplayName = "抛出异常",
        Version = new Version(1, 0, 0),
        Category = "基础",
        Description = "抛出异常，用于测试错误处理策略。"
    };

    public Type SettingsType => typeof(ThrowStepSettings);

    public object CreateDefaultSettings() => new ThrowStepSettings();

    public object LoadSettings(IReadOnlyDictionary<string, object?> parameters)
    {
        return new ThrowStepSettings
        {
            Message = SettingsMap.GetString(parameters, nameof(ThrowStepSettings.Message), "模拟 Step 异常")
        };
    }

    public IReadOnlyDictionary<string, object?> SaveSettings(object settings)
    {
        var typed = (ThrowStepSettings)settings;
        return new Dictionary<string, object?> { [nameof(ThrowStepSettings.Message)] = typed.Message };
    }

    public Task<TestStepResult> ExecuteAsync(TestStepExecutionContext context, object settings, CancellationToken cancellationToken)
    {
        var typed = (ThrowStepSettings)settings;
        throw new InvalidOperationException(typed.Message);
    }

}
