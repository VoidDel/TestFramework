using TestFramework.Abstractions.Plugins;
using TestFramework.Core.Plugins;
using TestFramework.Core.Resources;
using TestFramework.Plugins.BasicSteps;
using Xunit;

namespace TestFramework.Tests;

public sealed class FrameworkContractTests
{
    [Fact]
    public void Supports_AcceptsItsOwnContractAndAnythingOlder()
    {
        Assert.True(FrameworkContract.Supports(FrameworkContract.Version));
        Assert.True(FrameworkContract.Supports(new Version(1, 0)));
        Assert.True(FrameworkContract.Supports(new Version(0, 9)));
    }

    [Fact]
    public void Supports_RefusesAContractNewerThanThisBuild()
    {
        var newer = new Version(FrameworkContract.Version.Major + 1, 0);

        Assert.False(FrameworkContract.Supports(newer));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    public void Attribute_RejectsAVersionItCannotParse(string declared)
    {
        Assert.ThrowsAny<ArgumentException>(() => new TestFrameworkPluginAttribute(declared));
    }

    [Fact]
    public void LoadFromDirectory_PluginRequiringANewerContract_IsRefusedWithoutBeingConstructed()
    {
        using var directory = new TempPluginDirectory();
        directory.CopyFile(FixturePath("TestFramework.Tests.IncompatiblePlugin.dll"));
        var registry = new PluginRegistry();

        var report = new PluginDirectoryLoader(registry, new ResourcePluginRegistry())
            .LoadFromDirectory(directory.Path);

        Assert.Empty(report.StepPlugins);
        Assert.Empty(registry.Plugins);

        var failure = Assert.Single(report.Failures);
        Assert.IsType<NotSupportedException>(failure.Exception);
        // The fixture's constructor throws if it is ever run; a refusal message rather than that
        // exception is what proves the check happened from metadata, before any plugin code.
        Assert.Contains("99.0", failure.Message);
        Assert.Contains(FrameworkContract.Version.ToString(), failure.Message);
        Assert.Equal(new Version(99, 0), report.AssemblyContracts.Values.Single());
    }

    [Fact]
    public void LoadFromDirectory_RecordsTheContractDeclaredByAPluginItAccepts()
    {
        using var directory = new TempPluginDirectory();
        directory.Copy(typeof(DelayStepPlugin).Assembly);

        var report = new PluginDirectoryLoader(new PluginRegistry(), new ResourcePluginRegistry())
            .LoadFromDirectory(directory.Path);

        Assert.Empty(report.Failures);
        Assert.NotEmpty(report.StepPlugins);
        Assert.Equal(new Version(1, 0), Assert.Single(report.AssemblyContracts).Value);
    }

    private static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
}
