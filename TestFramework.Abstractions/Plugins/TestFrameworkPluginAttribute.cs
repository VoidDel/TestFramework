namespace TestFramework.Abstractions.Plugins;

/// <summary>
/// Declares, on a plugin assembly, the lowest framework contract version the plugin needs:
///
/// <code>[assembly: TestFrameworkPlugin("1.0")]</code>
///
/// Hosts read this before instantiating anything, so a plugin built against a contract the host
/// does not have is reported as an unsupported plugin instead of failing as a MissingMethodException
/// part-way through a run. Declaring it also lets a host adapt: it knows which optional parts of the
/// contract the plugin was compiled against.
///
/// An assembly without the attribute is treated as requiring <see cref="FrameworkContract.Baseline"/>,
/// which is what a plugin built before the attribute existed targeted.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class TestFrameworkPluginAttribute : Attribute
{
    public TestFrameworkPluginAttribute(string minimumFrameworkVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(minimumFrameworkVersion);
        if (!Version.TryParse(minimumFrameworkVersion, out var parsed))
        {
            throw new ArgumentException(
                $"'{minimumFrameworkVersion}' is not a valid framework version.",
                nameof(minimumFrameworkVersion));
        }

        MinimumFrameworkVersion = parsed;
    }

    public Version MinimumFrameworkVersion { get; }
}
