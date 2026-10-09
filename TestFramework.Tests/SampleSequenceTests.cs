using TestFramework.SequenceYaml;
using TestFramework.SequenceYaml.Validation;
using Xunit;

namespace TestFramework.Tests;

/// <summary>
/// Samples/sample-sequence.yaml is the template users copy, so it has to stay loadable and valid
/// as the schema and the validator rules evolve.
/// </summary>
public sealed class SampleSequenceTests
{
    [Fact]
    public void SampleSequence_LoadsValidatesAndRoundTrips()
    {
        var path = FindSamplePath();
        var service = new TestSequenceYamlService();

        var sequence = service.LoadFromFile(path);

        Assert.Empty(new TestSequenceValidator().Validate(sequence));

        var reloaded = service.Load(service.Save(sequence));
        Assert.Equal(sequence.Name, reloaded.Name);
        Assert.Equal(sequence.Items.Count, reloaded.Items.Count);
        Assert.Equal(sequence.Variables, reloaded.Variables);
    }

    /// <summary>
    /// Samples/flow-sequence.yaml shows every flow and judgment feature with built-in steps only:
    /// an operator prompt, a call, a group, element-wise limits from variables, a check, a loop and
    /// a poll. It has to validate cleanly and actually run to a pass, so the example in front of
    /// users is one that works.
    /// </summary>
    [Fact]
    public async Task FlowSample_ValidatesAndRunsToAPass()
    {
        var path = FindSamplePath("flow-sequence.yaml");
        var resolver = new FileSequenceResolver(Path.GetDirectoryName(path)!);
        var registry = new TestFramework.Core.Plugins.PluginRegistry();
        registry.Register(new TestFramework.Plugins.BasicSteps.LogStepPlugin());
        registry.Register(new TestFramework.Plugins.BasicSteps.CalculateStepPlugin());
        registry.Register(new TestFramework.Plugins.BasicSteps.PromptStepPlugin());
        var sequence = new TestSequenceYamlService().LoadFromFile(path);

        Assert.Empty(new TestSequenceValidator(registry, sequences: resolver).Validate(sequence));

        var result = await new TestFramework.Core.Execution.TestSequenceRunner(registry)
        {
            SequenceResolver = resolver,
            Operator = new AlwaysConfirms()
        }.RunAsync(sequence);

        Assert.Equal(TestFramework.Abstractions.Models.TestVerdict.Pass, result.Verdict);
        var precheck = result.ItemResults[1];
        Assert.Equal("shared-precheck", precheck.CalledSequenceId);
        var cells = result.ItemResults[2];
        var voltages = cells.Children[0];
        Assert.Equal(5, voltages.Measurements.Count);
        Assert.Equal("最大压差", voltages.Measurements[^1].Check);
        Assert.Equal(4, cells.Children.Count(child => child.ItemId == "channels"));
        Assert.Equal(4, result.ItemResults[3].MainResults[0].Attempts);
        Assert.Equal(100.0, result.FinalVariables["soc"]);
    }

    private static string FindSamplePath(string fileName = "sample-sequence.yaml")
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Samples", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Samples/{fileName} was not found above the test output directory.");
    }

    private sealed class AlwaysConfirms : TestFramework.Abstractions.Execution.IOperatorInteraction
    {
        public Task<TestFramework.Abstractions.Execution.OperatorResponse> PromptAsync(
            TestFramework.Abstractions.Execution.OperatorPrompt prompt,
            CancellationToken cancellationToken) =>
            Task.FromResult(new TestFramework.Abstractions.Execution.OperatorResponse { Option = prompt.Options[0] });
    }
}
