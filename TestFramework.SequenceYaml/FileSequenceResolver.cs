using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Models;

namespace TestFramework.SequenceYaml;

/// <summary>
/// Resolves a <c>call</c>'s path to a sequence file under one directory - usually the directory the
/// host keeps its sequences in.
///
/// Paths are relative to that directory, and one that leads out of it is refused: a sequence file
/// is reviewed as a test, not as a pointer into the rest of the disk, and a call that reached
/// <c>..\..\</c> could run a file nobody reviewed as part of this test. Each call loads the file
/// afresh, so an edit to a shared sequence applies to the next run without restarting the host.
/// </summary>
public sealed class FileSequenceResolver : ISequenceResolver
{
    private readonly string _baseDirectory;
    private readonly TestSequenceYamlService _yaml;

    public FileSequenceResolver(string baseDirectory, TestSequenceYamlService? yaml = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        _baseDirectory = Path.GetFullPath(baseDirectory);
        _yaml = yaml ?? new TestSequenceYamlService();
    }

    public TestSequence Resolve(string path)
    {
        return _yaml.LoadFromFile(FullPathOf(path));
    }

    /// <summary>Where <paramref name="path"/> points, or an exception when it leaves the base directory.</summary>
    public string FullPathOf(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(Path.Combine(_baseDirectory, path));
        var root = _baseDirectory.EndsWith(Path.DirectorySeparatorChar) ? _baseDirectory : _baseDirectory + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Sequence path '{path}' leads outside '{_baseDirectory}'.");
        }

        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"Sequence '{path}' does not exist under '{_baseDirectory}'.", full);
        }

        return full;
    }
}
