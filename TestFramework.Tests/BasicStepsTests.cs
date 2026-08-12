using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Models;
using TestFramework.Core.Execution;
using TestFramework.Core.Plugins;
using TestFramework.Plugins.BasicSteps;
using Xunit;

namespace TestFramework.Tests;

public sealed class BasicStepsTests
{
    [Theory]
    [InlineData(5.0, TestVerdict.Pass)]
    [InlineData(6.0, TestVerdict.Fail)]
    public async Task LimitCheck_ExecutesThroughTheRunner(double value, TestVerdict expected)
    {
        var registry = new PluginRegistry();
        registry.Register(new LimitCheckStepPlugin());
        var step = new TestStepDefinition
        {
            Id = "check",
            Name = "Check",
            PluginId = "basic.limit-check",
            PluginVersion = "1.0.0",
            Parameters =
            {
                ["Value"] = value,
                ["Min"] = 4.8,
                ["Max"] = 5.2
            }
        };
        var sequence = new TestSequence
        {
            Items =
            [
                new TestItemDefinition
                {
                    Name = "Item",
                    MainSteps = [step],
                    VerdictSource = new VerdictSource { StepId = step.Id }
                }
            ]
        };

        var result = await new TestSequenceRunner(registry).RunAsync(sequence);

        Assert.Equal(expected, result.Verdict);
    }
}
