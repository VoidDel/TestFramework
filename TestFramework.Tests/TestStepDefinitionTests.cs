using System.Collections;
using TestFramework.Abstractions.Models;
using TestFramework.SequenceYaml;
using Xunit;

namespace TestFramework.Tests;

public sealed class TestStepDefinitionTests
{
    [Fact]
    public void Clone_DeepCopiesYamlValuesWithoutIntroducingJsonElements()
    {
        var original = new TestStepDefinition
        {
            Parameters =
            {
                ["delayMs"] = 5000,
                ["settings"] = new Dictionary<string, object?>
                {
                    ["enabled"] = true,
                    ["values"] = new List<object?> { 1, "two" }
                }
            },
            VariableWrites =
            [
                new VariableWriteDefinition
                {
                    Name = "captured",
                    Value = new Dictionary<string, object?> { ["value"] = 42 }
                }
            ]
        };

        var clone = original.Clone();
        var sequence = new TestSequence
        {
            Items =
            [
                new TestItemDefinition
                {
                    MainSteps = [clone],
                    VerdictSource = new VerdictSource { StepId = clone.Id }
                }
            ]
        };
        var loaded = new TestSequenceYamlService().Load(new TestSequenceYamlService().Save(sequence));
        var loadedStep = Assert.Single(Assert.Single(loaded.Items).MainSteps);

        Assert.Equal(5000, Convert.ToInt32(loadedStep.Parameters["delayMs"]));
        AssertNoJsonElements(clone.Parameters);
        AssertNoJsonElements(clone.VariableWrites.Select(write => write.Value));
        Assert.NotSame(original.Parameters["settings"], clone.Parameters["settings"]);
    }

    private static void AssertNoJsonElements(object? value)
    {
        Assert.NotEqual("System.Text.Json.JsonElement", value?.GetType().FullName);
        if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                AssertNoJsonElements(entry.Value);
            }
        }
        else if (value is IEnumerable enumerable && value is not string)
        {
            foreach (var item in enumerable)
            {
                AssertNoJsonElements(item);
            }
        }
    }
}
