using TestFramework.Abstractions.Models;
using TestFramework.App.Services;
using TestFramework.SequenceYaml;
using Xunit;

namespace TestFramework.App.Tests;

public sealed class SequenceDocumentStoreTests
{
    [Fact]
    public async Task SaveAsync_RejectsAnExternallyModifiedFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "TestFrameworkTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var service = new TestSequenceYamlService();
            var file = Path.Combine(directory, "sequence.yaml");
            await File.WriteAllTextAsync(file, service.Save(ValidSequence("Original")));
            var store = new SequenceDocumentStore(service, directory, () => ValidSequence("New"));
            var document = store.Load().CurrentDocument;

            await Task.Delay(20);
            await File.WriteAllTextAsync(file, service.Save(ValidSequence("External")));
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(2));
            document.Sequence.Name = "Application";

            await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(document));
            Assert.Equal("External", service.LoadFromFile(file).Name);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static TestSequence ValidSequence(string name)
    {
        var step = new TestStepDefinition
        {
            Id = "main",
            Name = "Main",
            PluginId = "demo.step",
            PluginVersion = "1.0.0"
        };
        return new TestSequence
        {
            Name = name,
            Items =
            [
                new TestItemDefinition
                {
                    Name = "Item",
                    MainSteps = [step],
                    VerdictSource = new VerdictSource { StepId = step.Id }
                }
            ]
        };
    }
}
