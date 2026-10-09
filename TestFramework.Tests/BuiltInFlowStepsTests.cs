using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Models;
using TestFramework.Core.Execution;
using TestFramework.Core.Plugins;
using TestFramework.Plugins.BasicSteps;
using TestFramework.SequenceYaml.Validation;
using Xunit;

namespace TestFramework.Tests;

/// <summary>basic.calculate and basic.prompt, run through the real runner.</summary>
public sealed class BuiltInFlowStepsTests
{
    [Fact]
    public async Task Calculate_ComputesFromAListVariable_AndTheWriteKeepsIt()
    {
        var calculate = new TestStepDefinition
        {
            Id = "spread",
            Name = "压差",
            PluginId = "basic.calculate",
            Parameters = { ["Expression"] = "(max(${cells}) - min(${cells})) * 1000" },
            VariableWrites = { new VariableWriteDefinition { Name = "spreadMv", OutputKey = "value" } }
        };
        var sequence = Sequence(calculate);
        sequence.Variables["cells"] = new[] { 3.301, 3.295, 3.310 };

        var result = await RunAsync(sequence);

        var step = result.ItemResults[0].MainResults[0];
        Assert.Equal(TestVerdict.Pass, step.Verdict);
        Assert.Equal(15.0, (double)result.FinalVariables["spreadMv"]!, precision: 9);
    }

    [Fact]
    public async Task Calculate_ReceivesItsExpressionUnsubstituted()
    {
        // Substituting ${cells} would hand the plugin "System.Double[]" in place of the list.
        var calculate = new TestStepDefinition
        {
            Id = "count",
            Name = "count",
            PluginId = "basic.calculate",
            Parameters = { ["Expression"] = "count(${cells})" }
        };
        var sequence = Sequence(calculate);
        sequence.Variables["cells"] = new[] { 1.0, 2.0, 3.0 };

        var step = (await RunAsync(sequence)).ItemResults[0].MainResults[0];

        Assert.Equal(3.0, step.Outputs["value"]);
    }

    [Fact]
    public async Task Calculate_WithAnUndefinedVariable_IsAStepError()
    {
        var calculate = new TestStepDefinition
        {
            Id = "bad",
            Name = "bad",
            PluginId = "basic.calculate",
            Parameters = { ["Expression"] = "${nothing} + 1" }
        };

        var step = (await RunAsync(Sequence(calculate))).ItemResults[0].MainResults[0];

        Assert.Equal(TestVerdict.Error, step.Verdict);
        Assert.Contains("not defined", step.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Validator_ChecksAnExpressionParameter()
    {
        var registry = new PluginRegistry();
        registry.Register(new CalculateStepPlugin());
        var broken = Sequence(new TestStepDefinition
        {
            Id = "a",
            Name = "a",
            PluginId = "basic.calculate",
            Parameters = { ["Expression"] = "max(${cells}" }
        });
        var undefined = Sequence(new TestStepDefinition
        {
            Id = "b",
            Name = "b",
            PluginId = "basic.calculate",
            Parameters = { ["Expression"] = "${missing} * 2" }
        });

        Assert.Contains(new TestSequenceValidator(registry).Validate(broken), issue =>
            issue.Path.EndsWith("parameters.Expression", StringComparison.Ordinal) && issue.Message.Contains("not a valid expression"));
        Assert.Contains(new TestSequenceValidator(registry).Validate(undefined), issue =>
            issue.Path.EndsWith("parameters.Expression", StringComparison.Ordinal) && issue.Message.Contains("'missing'"));
    }

    [Theory]
    [InlineData("确定", TestVerdict.Pass)]
    [InlineData("取消", TestVerdict.Fail)]
    public async Task Prompt_PassesOnThePassOptionAndFailsOtherwise(string answer, TestVerdict verdict)
    {
        var operatorDesk = new ScriptedOperator(answer);
        var prompt = PromptStep(("Message", "接好线束后按确定"), ("Options", "确定, 取消"));

        var step = (await RunAsync(Sequence(prompt), operatorDesk)).ItemResults[0].MainResults[0];

        Assert.Equal(verdict, step.Verdict);
        Assert.Equal(answer, step.Outputs["response"]);
        Assert.Equal("worker-7", step.Outputs["respondedBy"]);
        var asked = Assert.Single(operatorDesk.Prompts);
        Assert.Equal("接好线束后按确定", asked.Message);
        Assert.Equal(["确定", "取消"], asked.Options);
        Assert.Equal("prompt", asked.StepName);
    }

    [Fact]
    public async Task Prompt_WithoutAnOperator_IsAnErrorRatherThanAnAutomaticYes()
    {
        var step = (await RunAsync(Sequence(PromptStep(("Message", "接好线束"))))).ItemResults[0].MainResults[0];

        Assert.Equal(TestVerdict.Error, step.Verdict);
        Assert.Contains("no operator interaction", step.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prompt_WithAPassOptionThatIsNotOffered_IsAnError()
    {
        var prompt = PromptStep(("Message", "?"), ("Options", "是,否"), ("PassOption", "确定"));

        var step = (await RunAsync(Sequence(prompt), new ScriptedOperator("是"))).ItemResults[0].MainResults[0];

        Assert.Equal(TestVerdict.Error, step.Verdict);
    }

    [Fact]
    public async Task Prompt_IsClosedByTheStepTimeout()
    {
        var prompt = PromptStep(("Message", "等待"));
        prompt.TimeoutMs = 100;

        var step = (await RunAsync(Sequence(prompt), new NeverAnswers())).ItemResults[0].MainResults[0];

        Assert.Equal(TestVerdict.Error, step.Verdict);
        Assert.Contains("timed out", step.ErrorMessage, StringComparison.Ordinal);
    }

    private static TestStepDefinition PromptStep(params (string Key, object? Value)[] parameters)
    {
        var step = new TestStepDefinition { Id = "prompt", Name = "prompt", PluginId = "basic.prompt" };
        foreach (var (key, value) in parameters)
        {
            step.Parameters[key] = value;
        }

        return step;
    }

    private static TestSequence Sequence(TestStepDefinition step) => new()
    {
        Name = "Sequence",
        Items = [new TestItemDefinition { Id = "item", Name = "item", MainSteps = [step], VerdictSource = new VerdictSource { StepId = step.Id } }]
    };

    private static Task<TestSequenceRunResult> RunAsync(TestSequence sequence, IOperatorInteraction? operatorDesk = null)
    {
        var registry = new PluginRegistry();
        registry.Register(new CalculateStepPlugin());
        registry.Register(new PromptStepPlugin());
        return new TestSequenceRunner(registry) { Operator = operatorDesk ?? NoOperatorInteraction.Instance }.RunAsync(sequence);
    }

    private sealed class ScriptedOperator(string answer) : IOperatorInteraction
    {
        public List<OperatorPrompt> Prompts { get; } = [];

        public Task<OperatorResponse> PromptAsync(OperatorPrompt prompt, CancellationToken cancellationToken)
        {
            Prompts.Add(prompt);
            return Task.FromResult(new OperatorResponse { Option = answer, RespondedBy = "worker-7" });
        }
    }

    private sealed class NeverAnswers : IOperatorInteraction
    {
        public async Task<OperatorResponse> PromptAsync(OperatorPrompt prompt, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}
