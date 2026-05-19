namespace TestFramework.Abstractions.Models;

public sealed class TestItemDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "New Test Item";

    public bool Enabled { get; set; } = true;

    public VerdictSource VerdictSource { get; set; } = new();

    public List<TestStepDefinition> InitSteps { get; set; } = [];

    public List<TestStepDefinition> MainSteps { get; set; } = [];

    public List<TestStepDefinition> CleanupSteps { get; set; } = [];
}
