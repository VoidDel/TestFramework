using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Plugins;
using TestFramework.Plugins.BasicSteps.Infrastructure;
using TestFramework.Plugins.BasicSteps.Settings;

namespace TestFramework.Plugins.BasicSteps;

/// <summary>
/// Asks the operator and waits: "接好线束后按确定". The answer is the output <c>response</c>; the
/// step passes when it is <c>PassOption</c> - the first option unless set - and fails otherwise,
/// so "取消" stops a test the operator could not set up instead of letting it measure nothing.
///
/// It goes through <see cref="TestStepExecutionContext.Operator"/>, which the host supplies. A host
/// with no operator makes the step an Error rather than an automatic yes: a prompt that answered
/// itself would let the test run on with the harness unconnected.
/// </summary>
public sealed class PromptStepPlugin : ITestStepPlugin
{
    public TestStepPluginDescriptor Descriptor { get; } = new()
    {
        PluginId = "basic.prompt",
        DisplayName = "操作员确认",
        Version = new Version(1, 0, 0),
        Category = "基础",
        Description = "提示操作员并等待选择；选择通过选项则通过，否则失败。"
    };

    public Type SettingsType => typeof(PromptStepSettings);

    public bool IsThreadSafe => true;

    public IReadOnlyList<StepParameterDescriptor> Parameters { get; } =
    [
        new StepParameterDescriptor
        {
            Name = nameof(PromptStepSettings.Message),
            Kind = StepParameterKind.String,
            DisplayName = "提示",
            Description = "显示给操作员的内容。",
            IsRequired = true
        },
        new StepParameterDescriptor
        {
            Name = nameof(PromptStepSettings.Title),
            Kind = StepParameterKind.String,
            DisplayName = "标题",
            DefaultValue = "操作员确认"
        },
        new StepParameterDescriptor
        {
            Name = nameof(PromptStepSettings.Options),
            Kind = StepParameterKind.String,
            DisplayName = "选项",
            Description = "逗号分隔，第一个为默认，例如 确定,取消。",
            DefaultValue = "确定"
        },
        new StepParameterDescriptor
        {
            Name = nameof(PromptStepSettings.PassOption),
            Kind = StepParameterKind.String,
            DisplayName = "通过选项",
            Description = "选中它则步骤通过；留空为第一个选项。"
        }
    ];

    public object CreateDefaultSettings() => new PromptStepSettings();

    public object LoadSettings(IReadOnlyDictionary<string, object?> parameters)
    {
        return new PromptStepSettings
        {
            Message = SettingsMap.GetString(parameters, nameof(PromptStepSettings.Message), string.Empty),
            Title = SettingsMap.GetString(parameters, nameof(PromptStepSettings.Title), "操作员确认"),
            Options = SettingsMap.GetString(parameters, nameof(PromptStepSettings.Options), "确定"),
            PassOption = SettingsMap.GetString(parameters, nameof(PromptStepSettings.PassOption), string.Empty)
        };
    }

    public IReadOnlyDictionary<string, object?> SaveSettings(object settings)
    {
        var typed = (PromptStepSettings)settings;
        return new Dictionary<string, object?>
        {
            [nameof(PromptStepSettings.Message)] = typed.Message,
            [nameof(PromptStepSettings.Title)] = typed.Title,
            [nameof(PromptStepSettings.Options)] = typed.Options,
            [nameof(PromptStepSettings.PassOption)] = typed.PassOption
        };
    }

    public async Task<TestStepResult> ExecuteAsync(TestStepExecutionContext context, object settings, CancellationToken cancellationToken)
    {
        var typed = (PromptStepSettings)settings;
        var startedAt = DateTimeOffset.Now;
        var options = typed.Options
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (options.Length == 0)
        {
            throw new ArgumentException("Parameter 'Options' must list at least one option.");
        }

        var passOption = string.IsNullOrWhiteSpace(typed.PassOption) ? options[0] : typed.PassOption.Trim();
        if (!options.Contains(passOption, StringComparer.Ordinal))
        {
            throw new ArgumentException($"PassOption '{passOption}' is not one of the options '{typed.Options}'.");
        }

        var response = await context.Operator.PromptAsync(
            new OperatorPrompt
            {
                Title = typed.Title,
                Message = typed.Message,
                Options = options,
                StepName = context.Step.Name
            },
            cancellationToken).ConfigureAwait(false);

        var result = new TestStepResult
        {
            Verdict = string.Equals(response.Option, passOption, StringComparison.Ordinal) ? TestVerdict.Pass : TestVerdict.Fail,
            StartedAt = startedAt,
            FinishedAt = DateTimeOffset.Now,
            Outputs = { ["response"] = response.Option }
        };
        if (response.RespondedBy is not null)
        {
            result.Outputs["respondedBy"] = response.RespondedBy;
        }

        return result;
    }
}
