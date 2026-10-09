namespace TestFramework.Abstractions.Models;

/// <summary>
/// One node of a sequence's test tree. It is one of three kinds:
///
/// - a <b>test item</b>, the usual kind: init, main and cleanup steps, judged by its verdict source
///   and checks;
/// - a <b>group</b>, holding further items in <see cref="Items"/> and no steps of its own - "charge
///   tests", "discharge tests" - whose verdict is that of its children;
/// - a <b>call</b>, which runs another sequence's items through <see cref="Call"/>.
///
/// <see cref="RunIf"/>, <see cref="Loop"/> and <see cref="Retry"/> apply to every kind.
/// </summary>
public sealed class TestItemDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "New Test Item";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// A condition that must be true for the item to run, evaluated just before it would; false
    /// records the item as skipped. Null always runs.
    /// </summary>
    public string? RunIf { get; set; }

    /// <summary>Runs the item once per index; see <see cref="LoopDefinition"/>.</summary>
    public LoopDefinition? Loop { get; set; }

    /// <summary>Runs the item again when it fails; see <see cref="RetryDefinition"/>.</summary>
    public RetryDefinition? Retry { get; set; }

    /// <summary>The items of a group. Non-empty makes this a group, which has no steps of its own.</summary>
    public List<TestItemDefinition> Items { get; set; } = [];

    /// <summary>Runs another sequence in place of steps; see <see cref="SequenceCallDefinition"/>.</summary>
    public SequenceCallDefinition? Call { get; set; }

    public VerdictSource VerdictSource { get; set; } = new();

    /// <summary>
    /// Further judgments of this item, each of a main step's output and each with its own limits -
    /// voltage and current in one item, each against its own range. Every one takes part in the
    /// item's verdict exactly as <see cref="VerdictSource"/> does, and each is recorded in
    /// <c>TestItemRunResult.Measurements</c>.
    ///
    /// A list beside <see cref="VerdictSource"/> rather than in place of it, because plugins read
    /// this type through the execution context and an existing member cannot change. A check always
    /// judges an output, so it needs an output key; taking a step's own verdict is what the verdict
    /// source is for.
    /// </summary>
    public List<VerdictSource> Checks { get; set; } = [];

    public List<TestStepDefinition> InitSteps { get; set; } = [];

    public List<TestStepDefinition> MainSteps { get; set; } = [];

    public List<TestStepDefinition> CleanupSteps { get; set; } = [];

    public bool IsGroup => Items.Count > 0;

    public bool IsCall => Call is not null;
}
