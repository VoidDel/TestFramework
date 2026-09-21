using System.Runtime.Loader;
using TestFramework.Core.Plugins;
using TestFramework.Core.Resources;
using TestFramework.Plugins.BasicSteps;
using Xunit;

namespace TestFramework.Tests;

/// <summary>
/// Exercises the directory scan against real assemblies on disk, so the load context, the managed
/// assembly filter and the release decision are covered as they actually run.
/// </summary>
public sealed class PluginDirectoryLoaderTests
{
    [Fact]
    public void LoadFromDirectory_LoadsPluginsAndIgnoresAssembliesThatHaveNone()
    {
        using var directory = new TempPluginDirectory();
        directory.Copy(typeof(DelayStepPlugin).Assembly);
        // A managed assembly carrying no plugin at all must be skipped in silence: a dependency
        // sitting in the plugin folder is normal, not a failure worth showing the operator.
        directory.Copy(typeof(TestFramework.Abstractions.Plugins.ITestStepPlugin).Assembly);

        var report = new PluginDirectoryLoader(new PluginRegistry(), new ResourcePluginRegistry())
            .LoadFromDirectory(directory.Path);

        Assert.Empty(report.Failures);
        Assert.Contains(report.StepPlugins, plugin => plugin.Descriptor.PluginId == "basic.delay");
        Assert.All(report.StepPlugins, plugin => Assert.True(report.PluginPaths.ContainsKey(plugin)));
    }

    [Fact]
    public void LoadFromDirectory_RegistersEachPluginIntoTheRegistry()
    {
        using var directory = new TempPluginDirectory();
        directory.Copy(typeof(DelayStepPlugin).Assembly);
        var registry = new PluginRegistry();

        var report = new PluginDirectoryLoader(registry, new ResourcePluginRegistry())
            .LoadFromDirectory(directory.Path);

        Assert.NotEmpty(report.StepPlugins);
        Assert.Equal(report.StepPlugins.Count, registry.Plugins.Count);
        Assert.True(registry.TryResolve("basic.delay", null, out _));
    }

    [Fact]
    public void LoadFromDirectory_LoadsEachAssemblyIntoOneIsolatedCollectibleContext()
    {
        using var directory = new TempPluginDirectory();
        directory.Copy(typeof(DelayStepPlugin).Assembly);

        var report = new PluginDirectoryLoader(new PluginRegistry(), new ResourcePluginRegistry())
            .LoadFromDirectory(directory.Path);

        // Every plugin from one file must come from a single context. Two contexts for one file
        // would give the same plugin type two identities, and type checks across them would fail.
        var contexts = report.StepPlugins
            .Select(plugin => AssemblyLoadContext.GetLoadContext(plugin.GetType().Assembly))
            .Distinct()
            .ToArray();

        var context = Assert.Single(contexts);
        Assert.NotNull(context);
        Assert.NotSame(AssemblyLoadContext.Default, context);
        Assert.True(context.IsCollectible);
    }

    [Fact]
    public void LoadFromDirectory_SamePluginDeployedTwice_NamesTheCopyItKept()
    {
        using var directory = new TempPluginDirectory();
        var first = directory.CopyInto("a-package", typeof(DelayStepPlugin).Assembly);
        var second = directory.CopyInto("b-package", typeof(DelayStepPlugin).Assembly);

        var report = new PluginDirectoryLoader(new PluginRegistry(), new ResourcePluginRegistry())
            .LoadFromDirectory(directory.Path);

        // The operator has two identical packages installed and needs to know which one is live.
        // "Already registered" on its own does not answer that.
        var failure = Assert.Single(
            report.Failures,
            candidate => candidate.Message.Contains("basic.delay", StringComparison.Ordinal));
        Assert.Contains(first, failure.Message, StringComparison.Ordinal);
        Assert.Contains(second, failure.Message, StringComparison.Ordinal);
        Assert.IsType<DuplicatePluginException>(failure.Exception);

        // Scan order is path order, so the same two folders resolve the same way on every machine.
        Assert.Equal(first, report.PluginPaths[Assert.Single(report.StepPlugins, plugin => plugin.Descriptor.PluginId == "basic.delay")]);
    }

    [Fact]
    public void LoadFromDirectory_MissingDirectory_ReturnsAnEmptyReport()
    {
        var report = new PluginDirectoryLoader(new PluginRegistry(), new ResourcePluginRegistry())
            .LoadFromDirectory(Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}"));

        Assert.Empty(report.StepPlugins);
        Assert.Empty(report.Failures);
    }
}
