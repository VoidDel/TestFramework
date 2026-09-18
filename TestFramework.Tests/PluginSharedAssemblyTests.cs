using System.Reflection;
using TestFramework.Core.Plugins;
using Xunit;

namespace TestFramework.Tests;

public sealed class PluginSharedAssemblyTests
{
    [Theory]
    [InlineData("TestFramework.Abstractions")]
    [InlineData("TestFramework.Plugin.Abstractions.UI")]
    [InlineData("Avalonia")]
    [InlineData("Avalonia.Base")]
    [InlineData("Avalonia.Controls")]
    [InlineData("avalonia.controls")]
    public void IsSharedWithHost_CoversTheContractsAndAvalonia(string name)
    {
        Assert.True(PluginAssemblyLoadContext.IsSharedWithHost(new AssemblyName(name)));
    }

    [Theory]
    [InlineData("Newtonsoft.Json")]
    [InlineData("YamlDotNet")]
    [InlineData("AvaloniaEdit")]
    [InlineData("TestFramework.Plugins.BasicSteps")]
    public void IsSharedWithHost_LeavesPluginDependenciesToThePlugin(string name)
    {
        Assert.False(PluginAssemblyLoadContext.IsSharedWithHost(new AssemblyName(name)));
    }
}
