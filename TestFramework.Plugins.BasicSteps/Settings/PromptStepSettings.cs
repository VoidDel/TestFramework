namespace TestFramework.Plugins.BasicSteps.Settings;

public sealed class PromptStepSettings
{
    public string Message { get; set; } = string.Empty;

    public string Title { get; set; } = "操作员确认";

    public string Options { get; set; } = "确定";

    public string PassOption { get; set; } = string.Empty;
}
