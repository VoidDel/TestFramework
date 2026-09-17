using TestFramework.Abstractions.Models;

namespace TestFramework.App.Services;

internal static class DisplayFormatters
{
    public static string FormatSection(StepSection section)
    {
        return section switch
        {
            StepSection.Init => "初始化",
            StepSection.Main => "主流程",
            StepSection.Cleanup => "清理",
            _ => section.ToString()
        };
    }

    public static string FormatVerdict(TestVerdict verdict)
    {
        return verdict switch
        {
            TestVerdict.None => "未运行",
            TestVerdict.Pass => "通过",
            TestVerdict.Fail => "失败",
            TestVerdict.Error => "错误",
            TestVerdict.Skipped => "跳过",
            TestVerdict.Inconclusive => "无结论",
            TestVerdict.Cancelled => "已取消",
            _ => verdict.ToString()
        };
    }
}
