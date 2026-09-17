using System.Reflection;
using TestFramework.Abstractions.Plugins;

namespace TestFramework.Core.Plugins;

/// <summary>
/// Reads the framework contract a plugin assembly declares.
/// </summary>
internal static class PluginContractReader
{
    private static readonly string AttributeFullName = typeof(TestFrameworkPluginAttribute).FullName!;

    /// <summary>
    /// Returns the lowest contract version <paramref name="assembly"/> needs, or
    /// <see cref="FrameworkContract.Baseline"/> when it declares none.
    ///
    /// The attribute is read as metadata rather than instantiated. A plugin built against a newer
    /// framework may carry an attribute shape this build cannot construct, and that plugin is
    /// exactly the one whose declared version must be readable - it is the one that has to be
    /// refused with a clear message.
    /// </summary>
    public static Version ReadMinimumFrameworkVersion(Assembly assembly)
    {
        foreach (var attribute in assembly.GetCustomAttributesData())
        {
            if (attribute.AttributeType.FullName != AttributeFullName ||
                attribute.ConstructorArguments.Count == 0)
            {
                continue;
            }

            if (attribute.ConstructorArguments[0].Value is string declared &&
                Version.TryParse(declared, out var version))
            {
                return version;
            }
        }

        return FrameworkContract.Baseline;
    }
}
