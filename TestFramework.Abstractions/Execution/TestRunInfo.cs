namespace TestFramework.Abstractions.Execution;

/// <summary>
/// Who and what a run was for - the facts a result has to carry to be traceable on its own, which
/// only the host knows. Passed to the runner and copied into <see cref="TestSequenceRunResult.RunInfo"/>.
///
/// A result without these answers "this unit failed" but not which unit, on which bench, run by
/// whom, against which revision of the test. Keeping them in variables (<c>${dutSerial}</c>) works
/// until a sequence names its variable differently; a result field means every report reads them
/// from the same place.
/// </summary>
public sealed class TestRunInfo
{
    public string? DutSerialNumber { get; set; }

    public string? Operator { get; set; }

    public string? StationId { get; set; }

    /// <summary>Where the sequence was loaded from.</summary>
    public string? SequenceFilePath { get; set; }

    /// <summary>
    /// A digest of the exact file that ran, such as <c>TestSequenceYamlService.ComputeHash</c>
    /// produces. A version number says which revision the authors meant; the hash says which bytes
    /// actually ran, including an edit nobody renumbered.
    /// </summary>
    public string? SequenceHash { get; set; }

    /// <summary>Anything else a host wants on the record: lot number, work order, fixture id.</summary>
    public Dictionary<string, string> Properties { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public TestRunInfo Clone() => new()
    {
        DutSerialNumber = DutSerialNumber,
        Operator = Operator,
        StationId = StationId,
        SequenceFilePath = SequenceFilePath,
        SequenceHash = SequenceHash,
        Properties = new Dictionary<string, string>(Properties, StringComparer.OrdinalIgnoreCase)
    };
}
