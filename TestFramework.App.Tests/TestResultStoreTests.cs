using System.Text.Json;
using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Models;
using TestFramework.App.Services;
using Xunit;

namespace TestFramework.App.Tests;

public sealed class TestResultStoreTests
{
    [Fact]
    public async Task SaveAsync_PersistsTheCompleteRunResultAsJson()
    {
        var directory = Path.Combine(Path.GetTempPath(), "TestFrameworkTests", Guid.NewGuid().ToString("N"));
        try
        {
            var result = new TestSequenceRunResult
            {
                SequenceId = "sequence",
                SequenceName = "Voltage/Test",
                Verdict = TestVerdict.Pass,
                StartedAt = DateTimeOffset.UtcNow,
                FinishedAt = DateTimeOffset.UtcNow.AddSeconds(1),
                FinalVariables = { ["voltage"] = 5.0 }
            };
            result.ItemResults.Add(new TestItemRunResult
            {
                ItemId = "item",
                ItemName = "Voltage",
                Verdict = TestVerdict.Pass,
                MainResults =
                {
                    new TestStepResult
                    {
                        StepId = "measure",
                        StepName = "Measure",
                        Verdict = TestVerdict.Inconclusive,
                        Exception = new InvalidOperationException("sensor failure"),
                        Outputs = { ["voltage"] = double.NaN }
                    }
                }
            });

            var file = await new TestResultStore(directory).SaveAsync(result);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(file));

            Assert.Equal("sequence", json.RootElement.GetProperty("SequenceId").GetString());
            Assert.Equal("Pass", json.RootElement.GetProperty("Verdict").GetString());
            Assert.Single(json.RootElement.GetProperty("ItemResults").EnumerateArray());
            var step = json.RootElement.GetProperty("ItemResults")[0].GetProperty("MainResults")[0];
            Assert.Equal("NaN", step.GetProperty("Outputs").GetProperty("voltage").GetString());
            Assert.Equal("sensor failure", step.GetProperty("Exception").GetProperty("message").GetString());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
