using System.Reflection;
using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Plugins;
using TestFramework.Core.Plugins;
using Xunit;

namespace TestFramework.Tests;

public sealed class PluginLoaderTests
{
    [Fact]
    public void LoadFromAssemblyWithReport_BrokenConstructor_DoesNotDiscardTheOtherPlugins()
    {
        var registry = new PluginRegistry();
        var loader = new PluginLoader(registry);

        var report = loader.LoadFromAssemblyWithReport(
            new StubAssembly([typeof(ThrowingPlugin), typeof(WorkingPlugin)]));

        Assert.Equal("working", Assert.Single(report.LoadedPlugins).Descriptor.PluginId);
        Assert.Single(report.Failures);
        Assert.Contains(nameof(ThrowingPlugin), report.Failures[0].Message);
    }

    [Fact]
    public void LoadFromAssemblyWithReport_PartialTypeLoad_KeepsLoadableTypesAndReportsTheFailure()
    {
        var registry = new PluginRegistry();
        var loader = new PluginLoader(registry);

        var report = loader.LoadFromAssemblyWithReport(
            new PartiallyLoadableAssembly(typeof(WorkingPlugin)));

        Assert.Equal("working", Assert.Single(report.LoadedPlugins).Descriptor.PluginId);
        Assert.IsType<ReflectionTypeLoadException>(Assert.Single(report.Failures).Exception);
    }

    private class StubAssembly : Assembly
    {
        private readonly Type[] _types;

        public StubAssembly(Type[] types) => _types = types;

        public override string Location => "stub-plugin.dll";

        public override Type[] GetTypes() => _types;
    }

    private sealed class PartiallyLoadableAssembly : StubAssembly
    {
        private readonly Type[] _loadable;

        public PartiallyLoadableAssembly(params Type[] loadable)
            : base(loadable) => _loadable = loadable;

        public override Type[] GetTypes()
        {
            throw new ReflectionTypeLoadException([.. _loadable, null], [null, new TypeLoadException("missing dependency")]);
        }
    }

    private sealed class ThrowingPlugin : StubPlugin
    {
        public ThrowingPlugin() => throw new InvalidOperationException("plugin constructor failed");

        public override string PluginId => "throwing";
    }

    private sealed class WorkingPlugin : StubPlugin
    {
        public override string PluginId => "working";
    }

    private abstract class StubPlugin : ITestStepPlugin
    {
        public abstract string PluginId { get; }

        public TestStepPluginDescriptor Descriptor => new()
        {
            PluginId = PluginId,
            DisplayName = PluginId,
            Version = new Version(1, 0, 0)
        };

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
