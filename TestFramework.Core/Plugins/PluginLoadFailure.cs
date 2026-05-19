namespace TestFramework.Core.Plugins;

public sealed class PluginLoadFailure
{
    public required string AssemblyPath { get; init; }

    public required string Message { get; init; }

    public Exception? Exception { get; init; }
}
