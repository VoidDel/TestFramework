namespace TestFramework.Core.Plugins;

/// <summary>
/// Thrown when a plugin id and version that are already registered turn up a second time.
///
/// The registry is the only place that can detect the collision, but it holds no file paths, so on
/// its own it can only say "already registered" - which leaves the operator with two identical
/// plugins and no way to tell which one is running. Carrying the winning instance lets the loader,
/// which does know where each plugin came from, name the file it kept and the file it ignored.
/// </summary>
public sealed class DuplicatePluginException : InvalidOperationException
{
    public DuplicatePluginException(string kind, string pluginId, Version version, object registeredPlugin)
        : base($"{kind} '{pluginId}' version '{version}' is already registered.")
    {
        Kind = kind;
        PluginId = pluginId;
        Version = version;
        RegisteredPlugin = registeredPlugin;
    }

    /// <summary>The plugin kind, as the registry names it - "Test step plugin" and so on.</summary>
    public string Kind { get; }

    public string PluginId { get; }

    public Version Version { get; }

    /// <summary>The instance that was registered first and is the one that stays.</summary>
    public object RegisteredPlugin { get; }

    /// <summary>
    /// The message naming both files. Kept separate from <see cref="Exception.Message"/> because the
    /// paths are the loader's knowledge, not the registry's.
    /// </summary>
    public string DescribeWithPaths(string? keptPath, string ignoredPath)
    {
        return string.IsNullOrWhiteSpace(keptPath)
            ? $"{Kind} '{PluginId}' version '{Version}' is already registered; the copy in '{ignoredPath}' was ignored."
            : $"{Kind} '{PluginId}' version '{Version}' is registered twice. Keeping the one in '{keptPath}'; the copy in '{ignoredPath}' was ignored. Plugin directories are scanned in path order, so the first path alphabetically wins.";
    }
}
