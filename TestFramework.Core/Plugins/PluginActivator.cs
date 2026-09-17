namespace TestFramework.Core.Plugins;

/// <summary>
/// Turns scanned types into plugin instances. Public so hosts can discover their own plugin kinds
/// (a settings editor, say) from the same scan. Step plugins and the three resource plugin kinds
/// differ only in the contract they implement, so they share one implementation of the rules that
/// matter: skip anything not constructible, and let one bad type fail on its own.
/// </summary>
public static class PluginActivator
{
    public static List<TPlugin> CreateAll<TPlugin>(
        IEnumerable<Type> types,
        string assemblyPath,
        ICollection<PluginLoadFailure>? failures)
        where TPlugin : class
    {
        var plugins = new List<TPlugin>();

        foreach (var type in types)
        {
            if (type.IsAbstract || type.IsInterface || !typeof(TPlugin).IsAssignableFrom(type))
            {
                continue;
            }

            // Isolated per type: a plugin whose constructor throws, or which has no parameterless
            // constructor, must not take the rest of its assembly down with it.
            try
            {
                if (Activator.CreateInstance(type) is TPlugin plugin)
                {
                    plugins.Add(plugin);
                }
            }
            catch (Exception ex) when (failures is not null)
            {
                failures.Add(new PluginLoadFailure
                {
                    AssemblyPath = assemblyPath,
                    Message = $"{type.FullName}: {ex.Message}",
                    Exception = ex
                });
            }
        }

        return plugins;
    }
}
