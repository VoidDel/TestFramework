using System.Diagnostics;
using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Plugins;
using TestFramework.Abstractions.Resources;
using TestFramework.Core.Execution;
using TestFramework.Core.Plugins;
using TestFramework.Core.Resources;
using TestFramework.SequenceYaml;
using Xunit;

namespace TestFramework.Tests;

/// <summary>
/// Stations in separate runner processes, each with its own session to one physical instrument,
/// taking turns on it through a lock file.
/// </summary>
public sealed class CrossProcessLeaseTests : IDisposable
{
    private readonly string _lockDirectory = Path.Combine(Path.GetTempPath(), "tf-locks-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_lockDirectory))
        {
            Directory.Delete(_lockDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task SecondHolder_WaitsUntilTheFirstReleases()
    {
        // Two providers stand for two processes: neither knows about the other's leases.
        var first = new CrossProcessLeaseProvider(_lockDirectory);
        var second = new CrossProcessLeaseProvider(_lockDirectory);

        var held = await first.LeaseAsync("dmm-1", CancellationToken.None);
        var waiting = second.LeaseAsync("DMM-1", CancellationToken.None);
        await Task.Delay(200);
        Assert.False(waiting.IsCompleted);

        held.Dispose();
        using var acquired = await waiting.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Waiting_StopsOnCancellation()
    {
        var first = new CrossProcessLeaseProvider(_lockDirectory);
        using var held = await first.LeaseAsync("dmm-1", CancellationToken.None);
        using var cancel = new CancellationTokenSource(150);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new CrossProcessLeaseProvider(_lockDirectory).LeaseAsync("dmm-1", cancel.Token));
    }

    [Fact]
    public async Task ALockHeldByAnotherProcess_IsWaitedFor()
    {
        // A real second process holds the lock file the way the provider does, then exits.
        var lockFile = Path.Combine(_lockDirectory, CrossProcessLeaseProvider.FileNameFor("dmm-1"));
        Directory.CreateDirectory(_lockDirectory);
        using var holder = StartHolder(lockFile, holdSeconds: 2);
        if (holder is null)
        {
            return; // no tool on this machine to hold a file lock from outside .NET
        }

        await WaitForFile(lockFile);
        await Task.Delay(300);

        var stopwatch = Stopwatch.StartNew();
        using var lease = await new CrossProcessLeaseProvider(_lockDirectory)
            .LeaseAsync("dmm-1", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(stopwatch.Elapsed > TimeSpan.FromMilliseconds(800), $"acquired after {stopwatch.Elapsed} while another process held it");
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    public async Task StationsInSeparateProcesses_TakeTurnsOnTheInstrumentTheyShare(bool shareLockName, int expectedPeak)
    {
        // Each "process" has its own registry, driver instance and session; only the physical
        // instrument behind them - and, when declared, the lock name - is common. The aliases differ.
        var physical = new PhysicalInstrument();
        var sessionA = Session("A", "dmm", shareLockName ? "dmm-gpib22" : null, physical);
        var sessionB = Session("B", "meter", shareLockName ? "dmm-gpib22" : null, physical);

        var results = await Task.WhenAll(
            sessionA.Session.RunAsync(Sequence("dmm")),
            sessionB.Session.RunAsync(Sequence("meter")));

        Assert.All(results, result => Assert.Equal(TestVerdict.Pass, result.Verdict));
        Assert.Equal(expectedPeak, physical.PeakUsers);
        await sessionA.Station.DisposeAsync();
        await sessionB.Station.DisposeAsync();
    }

    [Fact]
    public void StationYaml_LockName_RoundTrips_AndIsOmittedWhenUnset()
    {
        var service = new StationConfigurationYamlService();
        var station = new StationConfiguration
        {
            StationId = "A",
            Resources =
            [
                new StationResourceBinding { Alias = "dmm", DriverId = "d", Resource = "GPIB0::22", LockName = "dmm-gpib22" },
                new StationResourceBinding { Alias = "psu", DriverId = "d", Resource = "COM7" }
            ]
        };

        var saved = service.Save(station);
        var loaded = service.Load(saved);

        Assert.Equal("dmm-gpib22", loaded.Find("dmm")!.LockName);
        Assert.Null(loaded.Find("psu")!.LockName);
        Assert.Single(saved.Split('\n'), line => line.Contains("lockName", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("GPIB0::22::INSTR", "gpib0__22__instr.lock")]
    [InlineData(" DMM-1 ", "dmm-1.lock")]
    public void LockFileName_IsSafeAndCaseInsensitive(string name, string file)
    {
        Assert.Equal(file, CrossProcessLeaseProvider.FileNameFor(name));
    }

    private (TestRunSession Session, StationResourceHost Station) Session(string id, string alias, string? lockName, PhysicalInstrument physical)
    {
        var resources = new ResourcePluginRegistry();
        resources.RegisterInstrumentDriver(new SessionDriver(physical));
        var steps = new PluginRegistry();
        steps.Register(new UseStep());
        var station = new StationResourceHost(
            new StationConfiguration
            {
                StationId = id,
                Resources = [new StationResourceBinding { Alias = alias, DriverId = "demo.session", DriverVersion = "1.0.0", Resource = "GPIB0::22", LockName = lockName }]
            },
            resources,
            crossProcessLocks: new CrossProcessLeaseProvider(_lockDirectory));
        return (new TestRunSession(steps, resources, station), station);
    }

    private static TestSequence Sequence(string alias) => new()
    {
        Items =
        [
            new TestItemDefinition
            {
                Id = "item",
                Name = "item",
                MainSteps = [new TestStepDefinition { Id = "use", Name = "use", PluginId = "demo.use", Exclusive = [alias], Parameters = { ["instrument"] = alias } }],
                VerdictSource = new VerdictSource { StepId = "use" }
            }
        ]
    };

    private static Process? StartHolder(string lockFile, int holdSeconds)
    {
        ProcessStartInfo info;
        if (OperatingSystem.IsWindows())
        {
            var script = $"$f = [IO.File]::Open('{lockFile}', 'OpenOrCreate', 'ReadWrite', 'None'); Start-Sleep -Seconds {holdSeconds}; $f.Dispose()";
            info = new ProcessStartInfo("powershell", ["-NoProfile", "-NonInteractive", "-Command", script]);
        }
        else if (File.Exists("/usr/bin/flock") || File.Exists("/bin/flock"))
        {
            info = new ProcessStartInfo("flock", [lockFile, "sleep", holdSeconds.ToString()]);
        }
        else
        {
            return null;
        }

        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        return Process.Start(info);
    }

    private static async Task WaitForFile(string path)
    {
        for (var attempt = 0; attempt < 100 && !File.Exists(path); attempt++)
        {
            await Task.Delay(50);
        }
    }

    /// <summary>The instrument on the bench, which every station's session ends up talking to.</summary>
    private sealed class PhysicalInstrument
    {
        private int _users;
        private int _peak;

        public int PeakUsers => Volatile.Read(ref _peak);

        public async Task UseAsync()
        {
            var now = Interlocked.Increment(ref _users);
            int seen;
            while (now > (seen = Volatile.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, now, seen) != seen)
            {
            }

            await Task.Delay(150);
            Interlocked.Decrement(ref _users);
        }
    }

    /// <summary>A station's own session to the physical instrument - a different object per process.</summary>
    private sealed class InstrumentSession(PhysicalInstrument physical)
    {
        public Task UseAsync() => physical.UseAsync();
    }

    private sealed class SessionDriver(PhysicalInstrument physical) : IInstrumentDriverPlugin
    {
        public ResourcePluginDescriptor Descriptor { get; } = new() { PluginId = "demo.session", DisplayName = "session", Version = new Version(1, 0, 0) };

        public Type InstrumentType => typeof(InstrumentSession);

        public Task<object> CreateAsync(InstrumentDefinition definition, IResourceScope resources, CancellationToken cancellationToken) =>
            Task.FromResult<object>(new InstrumentSession(physical));
    }

    private sealed class UseStep : ITestStepPlugin
    {
        public TestStepPluginDescriptor Descriptor { get; } = new() { PluginId = "demo.use", DisplayName = "use", Version = new Version(1, 0, 0) };

        public Type SettingsType => typeof(Dictionary<string, object?>);

        public bool IsThreadSafe => true;

        public object CreateDefaultSettings() => new Dictionary<string, object?>();

        public object LoadSettings(IReadOnlyDictionary<string, object?> parameters) => parameters;

        public IReadOnlyDictionary<string, object?> SaveSettings(object settings) => (IReadOnlyDictionary<string, object?>)settings;

        public async Task<TestStepResult> ExecuteAsync(TestStepExecutionContext context, object settings, CancellationToken cancellationToken)
        {
            var alias = Convert.ToString(((IReadOnlyDictionary<string, object?>)settings)["instrument"])!;
            await context.Instruments.GetRequired<InstrumentSession>(alias).UseAsync();
            return new TestStepResult { Verdict = TestVerdict.Pass };
        }
    }
}
