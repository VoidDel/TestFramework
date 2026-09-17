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
    public void LoadFromDirectory_MissingDirectory_ReturnsAnEmptyReport()
    {
        var report = new PluginDirectoryLoader(new PluginRegistry(), new ResourcePluginRegistry())
            .LoadFromDirectory(Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}"));

        Assert.Empty(report.StepPlugins);
        Assert.Empty(report.Failures);
    }
}
