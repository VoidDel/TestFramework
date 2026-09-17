namespace TestFramework.Tests;

/// <summary>
/// A throwaway folder used as a plugin directory, so scans run against real files on disk.
/// </summary>
internal sealed class TempPluginDirectory : IDisposable
{
    public TempPluginDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tf-plugins-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Copy(System.Reflection.Assembly assembly) => CopyFile(assembly.Location);

    public void CopyFile(string source)
    {
        File.Copy(source, System.IO.Path.Combine(Path, System.IO.Path.GetFileName(source)));
    }

    public void Dispose()
    {
        // The plugin assemblies stay loaded, so the copies remain locked on Windows; the
        // directory is best-effort cleanup.
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
