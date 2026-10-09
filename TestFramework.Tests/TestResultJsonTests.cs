using System.Text.Json;
using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Models;
using TestFramework.Core.Results;
using Xunit;

namespace TestFramework.Tests;

public sealed class TestResultJsonTests
{
    [Fact]
    public void RoundTrip_KeepsTheWholeRecord()
    {
        var original = SampleResult();

        var read = TestResultJson.Deserialize(TestResultJson.Serialize(original));

        Assert.Equal("pack", read.SequenceId);
        Assert.Equal("B04", read.SequenceVersion);
        Assert.Equal(TestVerdict.Fail, read.Verdict);
        Assert.Equal("BMS-0042", read.RunInfo.DutSerialNumber);
        Assert.Equal("WO-17", read.RunInfo.Properties["WORKORDER"]);
        Assert.Equal(original.StartedAt, read.StartedAt);

        var item = Assert.Single(read.ItemResults);
        Assert.Equal(2, item.Attempt);
        Assert.Equal(TestVerdict.Fail, Assert.Single(item.PreviousAttempts).Verdict);
        Assert.Equal("grandchild", Assert.Single(Assert.Single(item.Children).Children).ItemId);

        var measurement = Assert.Single(item.Measurements);
        Assert.Equal(NumericComparison.GELE, measurement.Comparison);
        Assert.Equal(2.95, measurement.Value);
        Assert.Equal("2950 mV", measurement.RawValue);

        var step = Assert.Single(item.MainResults);
        Assert.Equal("1.2.0", step.PluginVersion);
        Assert.Equal("1.0.0", step.RequestedPluginVersion);
    }

    [Fact]
    public void Exceptions_AreRecordedByTypeMessageAndStack()
    {
        var result = SampleResult();
        var step = result.ItemResults[0].MainResults[0];
        try
        {
            throw new TimeoutException("CAN request timed out", new IOException("bus off"));
        }
        catch (Exception ex)
        {
            step.Exception = ex;
        }

        var read = TestResultJson.Deserialize(TestResultJson.Serialize(result)).ItemResults[0].MainResults[0];

        var recorded = Assert.IsType<RecordedException>(read.Exception);
        Assert.Equal("System.TimeoutException", recorded.OriginalType);
        Assert.Equal("CAN request timed out", recorded.Message);
        Assert.Contains(nameof(Exceptions_AreRecordedByTypeMessageAndStack), recorded.StackTrace, StringComparison.Ordinal);
        Assert.Equal("bus off", Assert.IsType<RecordedException>(recorded.InnerException).Message);

        // And a recorded exception written again keeps the original type name, not its own.
        var again = TestResultJson.Deserialize(TestResultJson.Serialize(TestResultJson.Deserialize(TestResultJson.Serialize(result))));
        Assert.Equal("System.TimeoutException", Assert.IsType<RecordedException>(again.ItemResults[0].MainResults[0].Exception).OriginalType);
    }

    [Fact]
    public void Values_OfAnyShape_AreWrittenAndReadBackAsPlainData()
    {
        var result = SampleResult();
        var outputs = result.ItemResults[0].MainResults[0].Outputs;
        outputs["cells"] = new[] { 3.3, 3.31 };
        outputs["signals"] = new Dictionary<string, double> { ["SOC"] = 95.5, ["soc"] = 1 };
        outputs["count"] = 80;
        outputs["nan"] = double.NaN;
        outputs["mode"] = StringComparison.Ordinal;
        outputs["at"] = new DateTimeOffset(2026, 10, 9, 8, 0, 0, TimeSpan.FromHours(8));
        outputs["odd"] = new Version(1, 2);

        var read = TestResultJson.Deserialize(TestResultJson.Serialize(result)).ItemResults[0].MainResults[0].Outputs;

        Assert.Equal(new List<object?> { 3.3, 3.31 }, read["cells"]);
        var signals = Assert.IsType<Dictionary<string, object?>>(read["signals"]);
        Assert.Equal(95.5, signals["SOC"]);

        // A whole double stays a double (written 1.0); an integer stays an integer.
        Assert.Equal(1.0, Assert.IsType<double>(signals["soc"]));
        Assert.Equal(80L, read["count"]);
        Assert.Equal("NaN", read["nan"]);
        Assert.Equal("Ordinal", read["mode"]);
        Assert.Equal("1.2", read["odd"]);

        // The outputs dictionary itself keeps the framework's case-insensitivity.
        Assert.Equal(80L, read["COUNT"]);
    }

    [Fact]
    public void WholeDouble_KeepsItsType_AcrossARoundTrip()
    {
        // A voltage of exactly 5 V must not come back from the report as the integer 5.
        var result = SampleResult();
        result.FinalVariables["voltage"] = 5.0;
        result.FinalVariables["cells"] = 80;

        var json = TestResultJson.Serialize(result);
        var read = TestResultJson.Deserialize(json);

        Assert.Equal(5.0, Assert.IsType<double>(read.FinalVariables["voltage"]));
        Assert.Equal(80L, Assert.IsType<long>(read.FinalVariables["cells"]));
        Assert.Contains("\"voltage\": 5.0", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Document_IsVersioned_AndOthersAreRefused()
    {
        var json = TestResultJson.Serialize(SampleResult());
        using var document = JsonDocument.Parse(json);

        Assert.Equal(TestResultJson.Format, document.RootElement.GetProperty("format").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("Fail", document.RootElement.GetProperty("result").GetProperty("verdict").GetString());

        Assert.Throws<InvalidDataException>(() => TestResultJson.Deserialize("""{"format":"other","schemaVersion":1}"""));
        Assert.Throws<InvalidDataException>(() =>
            TestResultJson.Deserialize(json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal)));
    }

    [Fact]
    public void SelfContainingValue_IsCutOffRatherThanOverflowingTheStack()
    {
        var result = SampleResult();
        var loop = new List<object?>();
        loop.Add(loop);
        result.ItemResults[0].MainResults[0].Outputs["loop"] = loop;

        var json = TestResultJson.Serialize(result);

        Assert.Contains("nested too deeply", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Streams_RoundTripToo()
    {
        using var stream = new MemoryStream();
        await TestResultJson.SerializeAsync(stream, SampleResult());
        stream.Position = 0;

        var read = await TestResultJson.DeserializeAsync(stream);

        Assert.Equal("pack", read.SequenceId);
    }

    private static TestSequenceRunResult SampleResult()
    {
        var result = new TestSequenceRunResult
        {
            SequenceId = "pack",
            SequenceName = "Pack",
            SequenceVersion = "B04",
            Verdict = TestVerdict.Fail,
            FrameworkVersion = "0.5.0",
            FrameworkContractVersion = "1.1",
            StartedAt = new DateTimeOffset(2026, 10, 9, 8, 0, 0, TimeSpan.FromHours(8)),
            FinishedAt = new DateTimeOffset(2026, 10, 9, 8, 1, 0, TimeSpan.FromHours(8)),
            RunInfo = new TestRunInfo { DutSerialNumber = "BMS-0042", Properties = { ["workOrder"] = "WO-17" } }
        };
        var item = new TestItemRunResult { ItemId = "cells", ItemName = "单体电压", Verdict = TestVerdict.Fail, Attempt = 2 };
        item.PreviousAttempts.Add(new TestItemRunResult { ItemId = "cells", Verdict = TestVerdict.Fail });
        var child = new TestItemRunResult { ItemId = "child" };
        child.Children.Add(new TestItemRunResult { ItemId = "grandchild" });
        item.Children.Add(child);
        item.Measurements.Add(new MeasurementResult
        {
            Name = "cells[17]",
            Check = "cells",
            Index = 17,
            RawValue = "2950 mV",
            Value = 2.95,
            Unit = "V",
            Comparison = NumericComparison.GELE,
            LowerLimit = 3.0,
            UpperLimit = 3.6,
            Verdict = TestVerdict.Fail
        });
        item.MainResults.Add(new TestStepResult
        {
            StepId = "read",
            StepName = "Read",
            Verdict = TestVerdict.Pass,
            PluginId = "bms.read",
            PluginVersion = "1.2.0",
            RequestedPluginVersion = "1.0.0"
        });
        result.ItemResults.Add(item);
        return result;
    }
}
