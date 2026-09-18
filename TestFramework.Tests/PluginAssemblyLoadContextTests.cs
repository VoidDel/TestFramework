using System.Reflection;
using TestFramework.Core.Plugins;
using TestFramework.Core.Resources;
using TestFramework.Plugins.BasicSteps;
using Xunit;

namespace TestFramework.Tests;

/// <summary>
/// A plugin assembly that depends on another file in the plugin directory - the settings-editor
/// assembly depending on the step assembly is the built-in case - must see the very instance the
/// directory scan registers. A private copy would give the shared types a second identity, and the
/// editor's cast of the settings object the step plugin created would fail.
/// </summary>
public sealed class PluginAssemblyLoadContextTests
{
    private const string StepAssemblyName = "TestFramework.Plugins.BasicSteps";

    [Fact]
    public void ResolvingASibling_ReturnsTheInstanceTheCatalogAlreadyHolds()
    {
        using var directory = new TempPluginDirectory();
        var (stepPath, otherPath) = Stage(directory);
        var held = PluginAssemblyCatalog.Load(stepPath).Assembly;

        var resolved = new PluginAssemblyLoadContext(otherPath).LoadFromAssemblyName(new AssemblyName(StepAssemblyName));

        Assert.Same(held, resolved);
    }

    [Fact]
    public void ResolvingASiblingBeforeTheScanReachesIt_IsTheInstanceTheScanThenRegisters()
    {
        using var directory = new TempPluginDirectory();
        var (_, otherPath) = Stage(directory);
        var early = new PluginAssemblyLoadContext(otherPath).LoadFromAssemblyName(new AssemblyName(StepAssemblyName));

        var report = new PluginDirectoryLoader(new PluginRegistry(), new ResourcePluginRegistry())
            .LoadFromDirectory(directory.Path);

        var delay = Assert.Single(report.StepPlugins, plugin => plugin.Descriptor.PluginId == "basic.delay");
        Assert.Same(early, delay.GetType().Assembly);
    }

    /// <summary>Two managed files side by side; the second stands in for any plugin that depends on the first.</summary>
    private static (string StepPath, string OtherPath) Stage(TempPluginDirectory directory)
    {
        directory.Copy(typeof(DelayStepPlugin).Assembly);
        directory.CopyFile(Path.Combine(AppContext.BaseDirectory, "Fixtures", "TestFramework.Tests.IncompatiblePlugin.dll"));
        return (
            Path.Combine(directory.Path, StepAssemblyName + ".dll"),
            Path.Combine(directory.Path, "TestFramework.Tests.IncompatiblePlugin.dll"));
    }
}
