using TestFramework.Abstractions.Models;
using TestFramework.SequenceYaml;
using Xunit;

namespace TestFramework.Tests;

public sealed class TestSequenceYamlServiceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(999)]
    public void LoadAndSave_RejectUnsupportedSchemaVersion(int version)
    {
        var service = new TestSequenceYamlService();
        Assert.Throws<InvalidDataException>(() => service.Load($"schemaVersion: {version}\nname: Sequence"));
        Assert.Throws<InvalidDataException>(() => service.Save(new TestSequence { SchemaVersion = version }));
    }

    [Fact]
    public void SaveAndLoad_RoundTripsSectionsAndVerdictSource()
    {
        var service = new TestSequenceYamlService();
        var sequence = new TestSequence
        {
            Id = "sequence",
            Name = "Sequence",
            Instruments =
            [
                new InstrumentDefinition
                {
                    Id = "can0",
                    DriverId = "vector.can",
                    DriverVersion = "1.0.0",
                    Resource = "CANcaseXL:0",
                    Settings = { ["bitrate"] = 500000 }
                }
            ],
            Transports =
            [
                new TransportDefinition
                {
                    Id = "ecu1-isotp",
                    TransportId = "isotp",
                    TransportVersion = "1.0.0",
                    Channel = "can0",
                    Settings = { ["requestId"] = 0x7E0 }
                }
            ],
            Services =
            [
                new TestServiceDefinition
                {
                    Id = "ecu1-uds",
                    ServiceId = "uds.client",
                    ServiceVersion = "1.0.0",
                    Transport = "ecu1-isotp"
                }
            ],
            Items =
            [
                new TestItemDefinition
                {
                    Id = "item",
                    Name = "Item",
                    InitSteps = [Step("init")],
                    MainSteps = [Step("main")],
                    CleanupSteps = [Step("cleanup")],
                    VerdictSource = new VerdictSource
                    {
                        StepId = "main",
                        OutputKey = "verdict",
                        JudgeType = VerdictJudgeType.Numeric,
                        LowerLimit = 4.8,
                        UpperLimit = 5.2,
                        SourceUnit = "mV",
                        Unit = "V"
                    }
                }
            ]
        };

        var loaded = service.Load(service.Save(sequence));
        var item = Assert.Single(loaded.Items);

        Assert.Equal("Sequence", loaded.Name);
        Assert.Equal("vector.can", Assert.Single(loaded.Instruments).DriverId);
        Assert.Equal("can0", Assert.Single(loaded.Transports).Channel);
        Assert.Equal("ecu1-isotp", Assert.Single(loaded.Services).Transport);
        Assert.Single(item.InitSteps);
        Assert.Single(item.MainSteps);
        Assert.Single(item.CleanupSteps);
        Assert.Equal("main", item.VerdictSource.StepId);
        Assert.Equal("verdict", item.VerdictSource.OutputKey);
        Assert.Equal(VerdictJudgeType.Numeric, item.VerdictSource.JudgeType);
        Assert.Equal(4.8, item.VerdictSource.LowerLimit);
        Assert.Equal(5.2, item.VerdictSource.UpperLimit);
        Assert.Equal("mV", item.VerdictSource.SourceUnit);
        Assert.Equal("V", item.VerdictSource.Unit);
    }

    [Fact]
    public void Load_RejectsUnknownOnErrorInsteadOfSilentlyChangingItToStop()
    {
        var yaml = """
            name: Sequence
            items:
            - name: Item
              main:
              - name: Step
                pluginId: demo.step
                onError: continuee
            """;

        var exception = Assert.Throws<InvalidDataException>(() => new TestSequenceYamlService().Load(yaml));

        Assert.Contains("continuee", exception.Message);
    }

    [Fact]
    public void Load_RejectsUnknownFieldsInsteadOfDiscardingThemOnSave()
    {
        var yaml = """
            name: Sequence
            futureSetting: keep-me
            items: []
            """;

        Assert.ThrowsAny<Exception>(() => new TestSequenceYamlService().Load(yaml));
    }

    [Theory]
    [InlineData("# important\nname: Sequence\nitems: []")]
    [InlineData("name: &sequenceName Sequence\nitems: []")]
    [InlineData("name: Sequence\nvariables:\n  value: &shared 1\n  copy: *shared\nitems: []")]
    public void Load_RejectsYamlSyntaxThatCannotBePreservedOnSave(string yaml)
    {
        Assert.Throws<InvalidDataException>(() => new TestSequenceYamlService().Load(yaml));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("Null")]
    [InlineData("NULL")]
    [InlineData("~")]
    [InlineData("")]
    public void SaveAndLoad_PreservesStringsThatYamlWouldResolveToNull(string value)
    {
        var service = new TestSequenceYamlService();
        var sequence = new TestSequence { Name = "Sequence" };
        sequence.Variables["variable"] = value;
        sequence.Instruments.Add(new InstrumentDefinition
        {
            Id = "instrument",
            DriverId = "demo.driver",
            Settings = { ["setting"] = value }
        });
        var step = Step("step");
        step.Parameters["parameter"] = value;
        sequence.Items.Add(new TestItemDefinition { Id = "item", Name = "Item", MainSteps = [step] });

        var reloaded = service.Load(service.Save(sequence));

        Assert.Equal(value, reloaded.Variables["variable"]);
        Assert.Equal(value, reloaded.Instruments[0].Settings["setting"]);
        Assert.Equal(value, reloaded.Items[0].MainSteps[0].Parameters["parameter"]);
    }

    [Fact]
    public void Load_TypesUnquotedScalarsAndKeepsQuotedOnesAsStrings()
    {
        var yaml = """
            schemaVersion: 1
            name: Sequence
            variables:
              voltage: 5.0
              count: 7
              flag: true
              big: 4294967296
              exponent: 1e3
              serial: "007"
              text: hello
              notANumber: .nan
              hex: 0x1F
            items:
            - id: item
              name: Item
              verdictSource: { stepId: step }
              main:
              - id: step
                name: Step
                pluginId: demo.step
                parameters:
                  DelayMs: 500
                  Value: ${voltage}
            """;

        var sequence = new TestSequenceYamlService().Load(yaml);

        Assert.Equal(5.0, Assert.IsType<double>(sequence.Variables["voltage"]));
        Assert.Equal(7, Assert.IsType<int>(sequence.Variables["count"]));
        Assert.True(Assert.IsType<bool>(sequence.Variables["flag"]));
        Assert.Equal(4294967296L, Assert.IsType<long>(sequence.Variables["big"]));
        Assert.Equal(1000.0, Assert.IsType<double>(sequence.Variables["exponent"]));
        Assert.Equal("007", Assert.IsType<string>(sequence.Variables["serial"]));
        Assert.Equal("hello", Assert.IsType<string>(sequence.Variables["text"]));
        Assert.Equal(".nan", Assert.IsType<string>(sequence.Variables["notANumber"]));
        Assert.Equal("0x1F", Assert.IsType<string>(sequence.Variables["hex"]));
        var parameters = sequence.Items[0].MainSteps[0].Parameters;
        Assert.Equal(500, Assert.IsType<int>(parameters["DelayMs"]));
        Assert.Equal("${voltage}", Assert.IsType<string>(parameters["Value"]));
    }

    [Theory]
    [InlineData("007")]
    [InlineData("123")]
    [InlineData("1.5")]
    [InlineData("1e3")]
    [InlineData("true")]
    [InlineData("False")]
    public void SaveAndLoad_PreservesStringsThatLookLikeNumbersOrBooleans(string value)
    {
        var service = new TestSequenceYamlService();
        var sequence = new TestSequence { Name = "Sequence" };
        sequence.Variables["variable"] = value;
        var step = Step("step");
        step.Parameters["parameter"] = value;
        sequence.Items.Add(new TestItemDefinition { Id = "item", Name = "Item", MainSteps = [step] });

        var reloaded = service.Load(service.Save(sequence));

        Assert.Equal(value, Assert.IsType<string>(reloaded.Variables["variable"]));
        Assert.Equal(value, Assert.IsType<string>(reloaded.Items[0].MainSteps[0].Parameters["parameter"]));
    }

    [Fact]
    public void SaveAndLoad_KeepsNumericTypes()
    {
        var service = new TestSequenceYamlService();
        var sequence = new TestSequence { Name = "Sequence" };
        sequence.Variables["wholeDouble"] = 5.0;
        sequence.Variables["fraction"] = 4.8;
        sequence.Variables["integer"] = 7;
        sequence.Variables["flag"] = true;

        var reloaded = service.Load(service.Save(sequence));

        // A whole double must not come back as an int: a plugin that received a double before the
        // file was saved would otherwise receive an int after it was reopened.
        Assert.Equal(5.0, Assert.IsType<double>(reloaded.Variables["wholeDouble"]));
        Assert.Equal(4.8, Assert.IsType<double>(reloaded.Variables["fraction"]));
        Assert.Equal(7, Assert.IsType<int>(reloaded.Variables["integer"]));
        Assert.True(Assert.IsType<bool>(reloaded.Variables["flag"]));
    }

    [Fact]
    public void Load_RejectsDuplicateKeysInsteadOfSilentlyOverwriting()
    {
        var service = new TestSequenceYamlService();

        Assert.ThrowsAny<Exception>(() => service.Load(
            """
            schemaVersion: 1
            name: Sequence
            variables:
              value: 1
              value: 2
            items: []
            """));
    }

    [Fact]
    public void Load_RejectsNestingDeeperThanTheSupportedLimit()
    {
        // Deserializing recurses per level, and a StackOverflowException would kill the process
        // rather than surface as a load failure, so the depth must be rejected before parsing.
        const int depth = 5000;
        var yaml = "schemaVersion: 1\nname: Sequence\nvariables:\n  value: " +
                   new string('[', depth) + new string(']', depth) + "\nitems: []";

        Assert.Throws<InvalidDataException>(() => new TestSequenceYamlService().Load(yaml));
    }

    private static TestStepDefinition Step(string id)
    {
        return new TestStepDefinition
        {
            Id = id,
            Name = id,
            PluginId = "demo.step",
            PluginVersion = "1.0.0"
        };
    }
}
