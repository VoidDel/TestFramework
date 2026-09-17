using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace TestFramework.Core.Plugins;

/// <summary>
/// Shared directory-scan helpers for the step and resource plugin loaders.
/// </summary>
internal static class PluginAssemblyScan
{
    /// <summary>
    /// Enumerates the DLLs in <paramref name="directory"/> that are managed assemblies. Plugin
    /// folders also contain native dependencies, and loading those only produces
    /// BadImageFormatException entries in the load report that read as plugin failures to the user.
    /// The metadata check is header-only and does not load the file into the process.
    /// </summary>
    public static IEnumerable<string> EnumerateCandidateAssemblies(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories))
        {
            if (IsManagedAssembly(file))
            {
                yield return file;
            }
        }
    }

    /// <summary>
    /// Returns the types of <paramref name="assembly"/>, keeping the ones that did load when some
    /// could not. A single type referencing a missing dependency would otherwise discard every
    /// plugin in the assembly. <paramref name="failure"/> carries the partial-load error so the
    /// caller can still report it.
    /// </summary>
    public static IReadOnlyList<Type> GetLoadableTypes(Assembly assembly, out ReflectionTypeLoadException? failure)
    {
        try
        {
            failure = null;
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            failure = ex;
            return ex.Types.OfType<Type>().ToArray();
        }
    }

    private static bool IsManagedAssembly(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new PEReader(stream);
            return reader.HasMetadata && reader.GetMetadataReader().IsAssembly;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return false;
        }
    }
}
