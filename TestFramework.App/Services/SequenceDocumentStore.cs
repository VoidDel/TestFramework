using TestFramework.Abstractions.Models;
using TestFramework.SequenceYaml;
using System.Security.Cryptography;

namespace TestFramework.App.Services;

public sealed class SequenceDocument
{
    public required TestSequence Sequence { get; init; }

    public string? FilePath { get; set; }

    public DateTimeOffset? LastKnownWriteTimeUtc { get; set; }

    public string? LastKnownContentHash { get; set; }

    public string Name => Sequence.Name;
}

public sealed class SequenceDocumentLoadFailure
{
    public required string FilePath { get; init; }

    public required string Message { get; init; }
}

public sealed class SequenceDocumentLoadResult
{
    public required IReadOnlyList<SequenceDocument> Documents { get; init; }

    public required SequenceDocument CurrentDocument { get; init; }

    public required IReadOnlyList<SequenceDocumentLoadFailure> Failures { get; init; }
}

public sealed class SequenceDocumentStore
{
    private readonly TestSequenceYamlService _yamlService;
    private readonly Func<TestSequence> _createDefaultSequence;

    public SequenceDocumentStore(
        TestSequenceYamlService yamlService,
        string directoryPath,
        Func<TestSequence> createDefaultSequence)
    {
        _yamlService = yamlService;
        DirectoryPath = directoryPath;
        _createDefaultSequence = createDefaultSequence;
    }

    public string DirectoryPath { get; }

    public SequenceDocumentLoadResult Load()
    {
        Directory.CreateDirectory(DirectoryPath);

        var documents = new List<SequenceDocument>();
        var failures = new List<SequenceDocumentLoadFailure>();

        foreach (var file in EnumerateSequenceFiles())
        {
            try
            {
                documents.Add(new SequenceDocument
                {
                    Sequence = _yamlService.LoadFromFile(file),
                    FilePath = file,
                    LastKnownWriteTimeUtc = File.GetLastWriteTimeUtc(file),
                    LastKnownContentHash = ComputeContentHash(file)
                });
            }
            catch (Exception ex)
            {
                failures.Add(new SequenceDocumentLoadFailure
                {
                    FilePath = file,
                    Message = ex.Message
                });
            }
        }

        if (documents.Count == 0)
        {
            documents.Add(CreateNew());
        }

        return new SequenceDocumentLoadResult
        {
            Documents = documents,
            CurrentDocument = documents[0],
            Failures = failures
        };
    }

    public SequenceDocument CreateNew()
    {
        return new SequenceDocument
        {
            Sequence = _createDefaultSequence()
        };
    }

    public async Task<string> SaveAsync(SequenceDocument document, CancellationToken cancellationToken = default)
    {
        var isNewFile = string.IsNullOrWhiteSpace(document.FilePath);
        var filePath = document.FilePath ?? GetUniqueSequenceFilePath(document.Sequence.Name);
        if (!isNewFile)
        {
            EnsureFileWasNotModifiedExternally(document, filePath);
        }

        await _yamlService.SaveToFileAsync(document.Sequence, filePath, cancellationToken).ConfigureAwait(false);
        document.FilePath = filePath;
        document.LastKnownWriteTimeUtc = File.GetLastWriteTimeUtc(filePath);
        document.LastKnownContentHash = ComputeContentHash(filePath);
        return document.FilePath;
    }

    public async Task<SequenceDocument> SaveCopyAsync(
        SequenceDocument source,
        IEnumerable<SequenceDocument> existingDocuments,
        CancellationToken cancellationToken = default)
    {
        var yaml = _yamlService.Save(source.Sequence);
        var copy = _yamlService.Load(yaml);
        copy.Name = MakeUniqueSequenceName(copy.Name, existingDocuments);

        var document = new SequenceDocument
        {
            Sequence = copy,
            FilePath = GetUniqueSequenceFilePath(copy.Name)
        };

        await _yamlService.SaveToFileAsync(copy, document.FilePath, cancellationToken).ConfigureAwait(false);
        document.LastKnownWriteTimeUtc = File.GetLastWriteTimeUtc(document.FilePath);
        document.LastKnownContentHash = ComputeContentHash(document.FilePath);
        return document;
    }

    public bool DeleteFile(SequenceDocument document)
    {
        if (string.IsNullOrWhiteSpace(document.FilePath) || !File.Exists(document.FilePath))
        {
            return false;
        }

        EnsureFileWasNotModifiedExternally(document, document.FilePath);
        File.Delete(document.FilePath);
        document.LastKnownWriteTimeUtc = null;
        document.LastKnownContentHash = null;
        return true;
    }

    private static void EnsureFileWasNotModifiedExternally(SequenceDocument document, string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new IOException($"Sequence file '{filePath}' was removed outside the application.");
        }

        var actualWriteTime = File.GetLastWriteTimeUtc(filePath);
        var actualHash = ComputeContentHash(filePath);
        if ((document.LastKnownWriteTimeUtc.HasValue && actualWriteTime != document.LastKnownWriteTimeUtc.Value.UtcDateTime) ||
            (document.LastKnownContentHash is not null && !string.Equals(actualHash, document.LastKnownContentHash, StringComparison.Ordinal)))
        {
            throw new IOException($"Sequence file '{filePath}' was modified outside the application. Reload it before saving.");
        }
    }

    private static string ComputeContentHash(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private IEnumerable<string> EnumerateSequenceFiles()
    {
        return Directory.EnumerateFiles(DirectoryPath, "*.yml")
            .Concat(Directory.EnumerateFiles(DirectoryPath, "*.yaml"))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
    }

    private string GetUniqueSequenceFilePath(string sequenceName)
    {
        Directory.CreateDirectory(DirectoryPath);

        var baseName = SanitizeFileName(string.IsNullOrWhiteSpace(sequenceName) ? "sequence" : sequenceName);
        var candidate = Path.Combine(DirectoryPath, $"{baseName}.yaml");
        for (var index = 1; File.Exists(candidate); index++)
        {
            candidate = Path.Combine(DirectoryPath, $"{baseName}{index}.yaml");
        }

        return candidate;
    }

    private static string MakeUniqueSequenceName(
        string name,
        IEnumerable<SequenceDocument> existingDocuments)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? "测试序列" : $"{name} 副本";
        var existing = new HashSet<string>(
            existingDocuments.Select(document => document.Sequence.Name),
            StringComparer.OrdinalIgnoreCase);

        if (!existing.Contains(baseName))
        {
            return baseName;
        }

        for (var index = 1; ; index++)
        {
            var candidate = $"{baseName}{index}";
            if (!existing.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(fileName
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray())
            .Trim();

        return string.IsNullOrWhiteSpace(sanitized) ? "sequence" : sanitized;
    }
}
