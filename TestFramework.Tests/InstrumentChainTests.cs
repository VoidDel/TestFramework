using System.Reflection;
using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Plugins;
using TestFramework.Abstractions.Resources;
using TestFramework.Core.Execution;
using TestFramework.Core.Plugins;
using TestFramework.Core.Resources;
using TestFramework.Instruments;
using TestFramework.Plugins.InstrumentSteps;
using TestFramework.Plugins.Simulation;
using TestFramework.SequenceYaml;
using TestFramework.SequenceYaml.Validation;
using Xunit;

namespace TestFramework.Tests;

/// <summary>
/// The reference chain end to end: Samples/instrument-sequence.yaml on Samples/instrument-station.yaml,
/// the generic steps driving the simulated instruments through the standard classes only. This is
/// what proves the classes are usable - a step written against IPowerSupply, a driver written
/// against it in another assembly, joined by nothing but the station file.
/// </summary>
public sealed class InstrumentChainTests
{
    [Fact]
    public async Task Sample_ValidatesAndPasses_OnTheSimulatedStation()
    {
        var (steps, resources) = InProcessRegistries();

        var result = await RunSampleAsync(steps, resources);

        Assert.True(result.Verdict == TestVerdict.Pass, Describe(result));
        Assert.Equal(TestVerdict.Pass, result.Cleanup!.Verdict);

        var supply = Item(result, "power-up");
        Assert.Equal(12.0, supply.Measurements[0].Value!.Value, precision: 6);
        // 12 V across the simulated 100 Ω load, judged in mA.
        Assert.Equal(120.0, supply.Measurements.Single(measurement => measurement.Check == "静态电流").Value!.Value, precision: 6);

        Assert.Equal(12000.0, Item(result, "supply-by-dmm").Measurements[0].Value!.Value, precision: 6);
        Assert.Equal(3.6, Item(result, "discharge").Measurements[0].Value!.Value, precision: 6);
        Assert.Equal(8, Item(result, "cell-voltages").Measurements.Count(measurement => measurement.Check != "最大压差"));

        var pwm = Item(result, "pwm-input");
        Assert.Equal(1000.0, pwm.Measurements[0].Value!.Value, precision: 3);
        // Edges fall on the 1 µs sample grid, so the 300 µs pulse is known to a sample either way.
        Assert.InRange(pwm.Measurements.Single(measurement => measurement.Check == "占空比").Value!.Value, 29.9, 30.1);

        var session = Assert.Single(Item(result, "diagnostic-session").MainResults);
        Assert.Equal("06 50 03 00 32 01 F4", session.Outputs["data"]);
        Assert.Equal(0x7E8L, session.Outputs["responseId"]);
    }

    [Fact]
    public async Task Sample_Passes_WithStepsAndDriversLoadedFromSeparateFolders()
    {
        // Each folder carries its own copy of TestFramework.Instruments, as dotnet publish leaves
        // it. Loaded per folder, the step's IPowerSupply and the driver's would be two different
        // types and every cast would fail; shared from the host, they are one.
        using var directory = new TempPluginDirectory();
        directory.CopyInto("InstrumentSteps", typeof(PowerSupplySetStepPlugin).Assembly);
        directory.CopyInto("InstrumentSteps", typeof(IPowerSupply).Assembly);
        directory.CopyInto("Simulation", typeof(SimulatedPowerSupplyDriver).Assembly);
        directory.CopyInto("Simulation", typeof(IPowerSupply).Assembly);
        var steps = new PluginRegistry();
        var resources = new ResourcePluginRegistry();

        var report = new PluginDirectoryLoader(steps, resources).LoadFromDirectory(directory.Path);

        Assert.Empty(report.Failures);
        Assert.Equal(12, report.StepPlugins.Count);
        Assert.Equal(6, report.InstrumentDrivers.Count);
        Assert.Single(report.Transports);
        // The plugins really are the folder copies, not the ones this test assembly references.
        Assert.All(report.StepPlugins, plugin => Assert.StartsWith(directory.Path, plugin.GetType().Assembly.Location, StringComparison.OrdinalIgnoreCase));
        Assert.All(report.InstrumentDrivers, plugin => Assert.Same(typeof(IPowerSupply).Assembly, typeof(IInstrument).Assembly));

        var result = await RunSampleAsync(steps, resources);

        Assert.True(result.Verdict == TestVerdict.Pass, Describe(result));
    }

    [Fact]
    public async Task Step_WhoseResourceIsMissing_SaysWhichClassItNeeds()
    {
        var (steps, _) = InProcessRegistries();
        var sequence = new TestSequence
        {
            Name = "Sequence",
            Items =
            [
                new TestItemDefinition
                {
                    Id = "read",
                    Name = "read",
                    VerdictSource = new VerdictSource { StepId = "measure" },
                    MainSteps =
                    [
                        new TestStepDefinition
                        {
                            Id = "measure",
                            Name = "measure",
                            PluginId = "psu.measure",
                            PluginVersion = "1.0.0",
                            Parameters = { ["Resource"] = "psu" }
                        }
                    ]
                }
            ]
        };

        var result = await new TestSequenceRunner(steps).RunAsync(sequence);

        var step = Assert.Single(Assert.Single(result.ItemResults).MainResults);
        Assert.Equal(TestVerdict.Error, step.Verdict);
        Assert.Contains("as a PowerSupply", step.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CanRequest_WithoutAReply_IsAFailOfTheDut()
    {
        var (steps, resources) = InProcessRegistries();
        var station = new StationConfiguration
        {
            StationId = "bench",
            Resources = [new StationResourceBinding { Alias = "can", Kind = ResourcePluginKind.Transport, DriverId = "sim.can", DriverVersion = "1.0.0" }]
        };
        var sequence = new TestSequence
        {
            Name = "Sequence",
            Requires = [new ResourceRequirement { Alias = "can", Kind = ResourcePluginKind.Transport, Class = InstrumentClasses.CanChannel }],
            Items =
            [
                new TestItemDefinition
                {
                    Id = "session",
                    Name = "session",
                    VerdictSource = new VerdictSource { StepId = "request" },
                    MainSteps =
                    [
                        new TestStepDefinition
                        {
                            Id = "request",
                            Name = "request",
                            PluginId = "can.request",
                            PluginVersion = "1.0.0",
                            Parameters = { ["Resource"] = "can", ["Id"] = "0x7E0", ["Data"] = "02 10 03", ["ResponseId"] = "0x7E8", ["TimeoutMs"] = 20 }
                        }
                    ]
                }
            ]
        };

        await using var host = new StationResourceHost(station, resources);
        await using var scope = await host.BeginRunAsync(sequence);
        var result = await new TestSequenceRunner(steps, resources: scope).RunAsync(sequence);

        Assert.Equal(TestVerdict.Fail, result.Verdict);
        Assert.Contains("No reply", Assert.Single(Assert.Single(result.ItemResults).MainResults).ErrorMessage, StringComparison.Ordinal);
        var channel = (SimulatedCanChannel)scope.Scope.Transports.GetRequired<ICanChannel>("can");
        Assert.Equal("0x7E0 [3] 02 10 03", Assert.Single(channel.Sent).ToString());
    }

    [Fact]
    public async Task SimulatedSupply_TripsItsProtection_AndTheStepSaysSo()
    {
        var supply = (SimulatedPowerSupply)await new SimulatedPowerSupplyDriver().CreateAsync(
            new InstrumentDefinition { Id = "psu" }, EmptyResourceScope.Instance, CancellationToken.None);

        await supply.SetOverVoltageProtectionAsync(13.0);
        await supply.SetVoltageAsync(14.0);
        await supply.SetOutputEnabledAsync(true);

        var status = await supply.QueryStatusAsync();
        Assert.True(status.ProtectionTripped);
        Assert.False(status.OutputEnabled);
        Assert.Equal(0.0, await supply.MeasureVoltageAsync());

        await supply.ClearProtectionAsync();
        await supply.SetVoltageAsync(12.0);
        await supply.SetCurrentLimitAsync(0.05);
        await supply.SetOutputEnabledAsync(true);

        // 12 V into 100 Ω wants 120 mA; limited to 50 mA, the supply holds the current instead.
        Assert.Equal(RegulationMode.ConstantCurrent, (await supply.QueryStatusAsync()).Mode);
        Assert.Equal(5.0, await supply.MeasureVoltageAsync(), precision: 9);
    }

    private static async Task<TestSequenceRunResult> RunSampleAsync(PluginRegistry steps, ResourcePluginRegistry resources)
    {
        var sequence = new TestSequenceYamlService().LoadFromFile(SamplePath("instrument-sequence.yaml"));
        var station = new StationConfigurationYamlService().LoadFromFile(SamplePath("instrument-station.yaml"));

        var issues = new TestSequenceValidator(steps, resources, station).Validate(sequence);
        Assert.True(issues.Count == 0, string.Join(Environment.NewLine, issues.Select(issue => $"{issue.Path}: {issue.Message}")));

        await using var host = new StationResourceHost(station, resources);
        await using var scope = await host.BeginRunAsync(sequence);
        return await new TestSequenceRunner(steps, resources: scope).RunAsync(sequence);
    }

    private static (PluginRegistry Steps, ResourcePluginRegistry Resources) InProcessRegistries()
    {
        var steps = new PluginRegistry();
        foreach (var plugin in Instances<ITestStepPlugin>(typeof(PowerSupplySetStepPlugin).Assembly))
        {
            steps.Register(plugin);
        }

        var resources = new ResourcePluginRegistry();
        foreach (var driver in Instances<IInstrumentDriverPlugin>(typeof(SimulatedPowerSupplyDriver).Assembly))
        {
            resources.RegisterInstrumentDriver(driver);
        }

        foreach (var transport in Instances<ITransportPlugin>(typeof(SimulatedPowerSupplyDriver).Assembly))
        {
            resources.RegisterTransport(transport);
        }

        return (steps, resources);
    }

    private static IEnumerable<T> Instances<T>(Assembly assembly) =>
        assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(T).IsAssignableFrom(type))
            .Select(type => (T)Activator.CreateInstance(type)!);

    private static TestItemRunResult Item(TestSequenceRunResult result, string id) =>
        result.ItemResults.Single(item => item.ItemId == id);

    private static string Describe(TestSequenceRunResult result) =>
        string.Join(Environment.NewLine, result.ItemResults.Append(result.Cleanup).OfType<TestItemRunResult>().Select(item =>
            $"{item.ItemId}: {item.Verdict} {item.ErrorMessage} | " +
            string.Join("; ", item.InitResults.Concat(item.MainResults).Concat(item.CleanupResults)
                .Select(step => $"{step.StepId}={step.Verdict} {step.ErrorMessage}")) + " | " +
            string.Join("; ", item.Measurements.Select(measurement => $"{measurement.Name}={measurement.Value} {measurement.Verdict}"))));

    private static string SamplePath(string fileName)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "Samples", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Samples/{fileName} was not found above the test output directory.");
    }
}
