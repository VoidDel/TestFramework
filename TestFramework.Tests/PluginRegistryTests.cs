using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Plugins;
using TestFramework.Core.Plugins;
using System.Runtime.Loader;
using TestFramework.Plugins.BasicSteps;
using Xunit;

namespace TestFramework.Tests;

public sealed class PluginRegistryTests
{
    [Fact]
    public void LoadFromAssembly_UsesAnIsolatedCollectibleLoadContext()
    {
        var plugins = new PluginLoader(new PluginRegistry())
            .LoadFromAssembly(typeof(DelayStepPlugin).Assembly.Location);

        var plugin = Assert.Single(plugins, candidate => candidate.Descriptor.PluginId == "basic.delay");
        var loadContext = AssemblyLoadContext.GetLoadContext(plugin.GetType().Assembly);

        Assert.NotNull(loadContext);
        Assert.NotSame(AssemblyLoadContext.Default, loadContext);
        Assert.True(loadContext.IsCollectible);
    }

    [Fact]
    public void GetRequired_ReturnsRequestedVersion()
    {
        var registry = new PluginRegistry();
        var version1 = new StubPlugin("demo.step", new Version(1, 0, 0));
        var version2 = new StubPlugin("demo.step", new Version(2, 0, 0));

        registry.Register(version1);
        registry.Register(version2);

        Assert.Same(version1, registry.GetRequired("demo.step", "1.0.0"));
        Assert.Same(version2, registry.GetRequired("demo.step", "2.0.0"));
        Assert.Same(version2, registry.GetRequired("demo.step"));
    }

    [Fact]
    public void Register_RejectsDuplicatePluginVersion()
    {
        var registry = new PluginRegistry();
        registry.Register(new StubPlugin("demo.step", new Version(1, 0, 0)));

        Assert.Throws<InvalidOperationException>(() =>
            registry.Register(new StubPlugin("demo.step", new Version(1, 0, 0))));
    }

    [Fact]
    public void TryResolve_ExactVersionInstalled_RunsThatVersionAndReportsNoSubstitution()
    {
        var registry = new PluginRegistry();
        registry.Register(new StubPlugin("demo.step", new Version(1, 0, 0)));
        registry.Register(new StubPlugin("demo.step", new Version(1, 2, 0)));

        Assert.True(registry.TryResolve("demo.step", "1.0.0", out var resolution));

        Assert.Equal(new Version(1, 0, 0), resolution.ResolvedVersion);
        Assert.Equal(PluginVersionMatch.Exact, resolution.Match);
        Assert.False(resolution.IsSubstituted);
    }

    [Fact]
    public void TryResolve_RequestedVersionRemoved_SubstitutesTheNewestCompatibleBuild()
    {
        var registry = new PluginRegistry();
        registry.Register(new StubPlugin("demo.step", new Version(1, 2, 0)));
        registry.Register(new StubPlugin("demo.step", new Version(2, 0, 0)));

        Assert.True(registry.TryResolve("demo.step", "1.0.0", out var resolution));

        Assert.Equal(new Version(1, 2, 0), resolution.ResolvedVersion);
        Assert.True(resolution.IsSubstituted);
        Assert.Equal("1.0.0", resolution.RequestedVersion);
    }

    [Fact]
    public void TryResolve_OnlyADifferentMajorVersionInstalled_Fails()
    {
        var registry = new PluginRegistry();
        registry.Register(new StubPlugin("demo.step", new Version(2, 0, 0)));

        Assert.False(registry.TryResolve("demo.step", "1.0.0", out _));
    }

    [Fact]
    public void DescribeMissing_DistinguishesAVersionMismatchFromAnUninstalledPlugin()
    {
        var registry = new PluginRegistry();
        registry.Register(new StubPlugin("demo.step", new Version(2, 0, 0)));

        var mismatch = registry.DescribeMissing("demo.step", "1.0.0");
        Assert.Contains("2.0.0", mismatch);
        Assert.Contains("1.0.0", mismatch);

        Assert.DoesNotContain("Installed versions", registry.DescribeMissing("absent.step", "1.0.0"));
    }

    [Fact]
    public void Register_BlankPluginId_IsRejectedAtRegistrationRatherThanBecomingUnaddressable()
    {
        var registry = new PluginRegistry();

        Assert.Throws<InvalidOperationException>(() => registry.Register(new StubPlugin("   ", new Version(1, 0, 0))));
    }

    private sealed class StubPlugin : ITestStepPlugin
    {
        public StubPlugin(string pluginId, Version version)
        {
            Descriptor = new TestStepPluginDescriptor
            {
                PluginId = pluginId,
                DisplayName = pluginId,
                Version = version
            };
        }

        public TestStepPluginDescriptor Descriptor { get; }

        public Type SettingsType => typeof(object);

        public object CreateDefaultSettings() => new();

        public object LoadSettings(IReadOnlyDictionary<string, object?> parameters) => new();

        public IReadOnlyDictionary<string, object?> SaveSettings(object settings) => new Dictionary<string, object?>();

        public Task<TestStepResult> ExecuteAsync(
            TestStepExecutionContext context,
            object settings,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new TestStepResult { Verdict = TestVerdict.Pass });
        }
    }
}
