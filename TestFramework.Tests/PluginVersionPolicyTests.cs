using TestFramework.Abstractions.Plugins;
using Xunit;

namespace TestFramework.Tests;

public sealed class PluginVersionPolicyTests
{
    [Theory]
    // The requested version is installed: it wins, so a sequence naming an installed version
    // always runs exactly that code.
    [InlineData("1.0.0", "1.0.0,1.0.1,1.1.0", "1.0.0", PluginVersionMatch.Exact)]
    [InlineData("1.1.0", "1.0.0,1.0.1,1.1.0", "1.1.0", PluginVersionMatch.Exact)]
    // Gone, but a newer same-major build is present: substituted, and reported as such.
    [InlineData("1.0.0", "1.0.1", "1.0.1", PluginVersionMatch.Compatible)]
    [InlineData("1.0.0", "1.0.1,1.2.3", "1.2.3", PluginVersionMatch.Compatible)]
    [InlineData("1.0.0", "1.0.1,2.0.0", "1.0.1", PluginVersionMatch.Compatible)]
    // No version requested: the newest installed build.
    [InlineData(null, "1.0.0,2.1.0", "2.1.0", PluginVersionMatch.Unspecified)]
    [InlineData("", "1.0.0,2.1.0", "2.1.0", PluginVersionMatch.Unspecified)]
    public void TrySelect_ResolvesToTheExpectedVersion(
        string? requested,
        string installed,
        string expected,
        PluginVersionMatch expectedMatch)
    {
        Assert.True(PluginVersionPolicy.TrySelect(Versions(installed), requested, out var selected, out var match));
        Assert.Equal(Version.Parse(expected), selected);
        Assert.Equal(expectedMatch, match);
    }

    [Theory]
    // A different major version may change behaviour the sequence depends on.
    [InlineData("1.0.0", "2.0.0")]
    [InlineData("2.0.0", "1.9.9")]
    // Never downgrade: an older build may not have what the sequence uses.
    [InlineData("1.5.0", "1.4.0")]
    [InlineData("1.5.0", "1.0.0,1.4.9")]
    // Nothing installed, or a version the file cannot express.
    [InlineData("1.0.0", "")]
    [InlineData("not-a-version", "1.0.0")]
    public void TrySelect_RefusesRatherThanSubstituteSomethingIncompatible(string requested, string installed)
    {
        Assert.False(PluginVersionPolicy.TrySelect(Versions(installed), requested, out _, out _));
    }

    [Fact]
    public void DescribeInstalled_ListsVersionsNewestFirst()
    {
        Assert.Equal("2.0.0, 1.1.0, 1.0.0", PluginVersionPolicy.DescribeInstalled(Versions("1.0.0,2.0.0,1.1.0")));
        Assert.Equal("none installed", PluginVersionPolicy.DescribeInstalled([]));
    }

    private static Version[] Versions(string csv) => csv.Length == 0
        ? []
        : csv.Split(',').Select(Version.Parse).ToArray();
}
