using TestFramework.SequenceYaml;
using TestFramework.SequenceYaml.Validation;
using Xunit;

namespace TestFramework.Tests;

/// <summary>
/// Samples/sample-sequence.yaml is the template users copy, so it has to stay loadable and valid
/// as the schema and the validator rules evolve.
/// </summary>
public sealed class SampleSequenceTests
{
    [Fact]
    public void SampleSequence_LoadsValidatesAndRoundTrips()
    {
        var path = FindSamplePath();
        var service = new TestSequenceYamlService();

        var sequence = service.LoadFromFile(path);

        Assert.Empty(new TestSequenceValidator().Validate(sequence));

        var reloaded = service.Load(service.Save(sequence));
        Assert.Equal(sequence.Name, reloaded.Name);
        Assert.Equal(sequence.Items.Count, reloaded.Items.Count);
        Assert.Equal(sequence.Variables, reloaded.Variables);
    }

    private static string FindSamplePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Samples", "sample-sequence.yaml");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Samples/sample-sequence.yaml was not found above the test output directory.");
    }
}
