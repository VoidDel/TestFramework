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
        // Ordered, because when the same plugin id and version is deployed twice the first one
        // scanned is the one that stays. Left to the file system that choice varies by machine and
        // by the order the folders happened to be written, which makes the duplicate report - and
        // the run itself - irreproducible. Path order is arbitrary but at least it is stable.
        var files = Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            // A copy of an assembly the host shares (the contracts, Avalonia) is not a plugin and
            // must never be loaded privately - see PluginAssemblyLoadContext.IsSharedWithHost.
            // It still turns up in plugin folders, because that is what dotnet publish produces.
            if (TryReadAssemblyName(file, out var name) && !PluginAssemblyLoadContext.IsSharedWithHost(name))
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

    /// <summary>Reads the assembly name from the file's metadata; false for anything that is not a managed assembly.</summary>
    private static bool TryReadAssemblyName(string path, out AssemblyName name)
    {
        name = null!;
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new PEReader(stream);
            if (!reader.HasMetadata)
            {
                return false;
            }

            var metadata = reader.GetMetadataReader();
            if (!metadata.IsAssembly)
            {
                return false;
            }

            name = new AssemblyName(metadata.GetString(metadata.GetAssemblyDefinition().Name));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return false;
        }
    }
}
