using TestFramework.Abstractions.Models;
using TestFramework.SequenceYaml;
using Xunit;

namespace TestFramework.Tests;

public sealed class TestSequenceYamlServiceTests
{
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
                        OutputKey = "verdict"
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
