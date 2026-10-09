using TestFramework.Abstractions.Models;

namespace TestFramework.Abstractions.Execution;

/// <summary>
/// Finds the sequence a <see cref="SequenceCallDefinition"/> names. The runner has no idea where
/// sequences live - files, a database, an embedded resource - so the host supplies this, and
/// <c>TestFramework.SequenceYaml</c> provides the file-based one.
/// </summary>
public interface ISequenceResolver
{
    /// <summary>
    /// The sequence <paramref name="path"/> names. Throws when there is none; the runner records the
    /// message on the calling item.
    /// </summary>
    TestSequence Resolve(string path);
}
