using System.Reflection;
using System.Reflection.Emit;
using TestFramework.Core.Resources;
using Xunit;

namespace TestFramework.Tests;

public sealed class ResourcePluginLoaderTests
{
    [Fact]
    public void LoadFromAssembly_ReportsGetTypesFailureInsteadOfThrowing()
    {
        var loader = new ResourcePluginLoader(new ResourcePluginRegistry());
        var assembly = new ThrowingAssembly();

        var report = loader.LoadFromAssembly(assembly);

        Assert.Empty(report.InstrumentDrivers);
        Assert.Single(report.Failures);
        Assert.IsType<ReflectionTypeLoadException>(report.Failures[0].Exception);
    }

    private sealed class ThrowingAssembly : Assembly
    {
        public override string Location => "broken-plugin.dll";

        public override Type[] GetTypes()
        {
            throw new ReflectionTypeLoadException([], []);
        }
    }
}
