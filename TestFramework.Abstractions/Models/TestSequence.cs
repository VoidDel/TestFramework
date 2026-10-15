namespace TestFramework.Abstractions.Models;

public sealed class TestSequence
{
    public int SchemaVersion { get; set; } = 1;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "New Test Sequence";

    /// <summary>
    /// The sequence's own revision, as its authors number it ("2.3", "B04"). Copied into every run
    /// result, so a result says which revision of the test produced it. Null when not numbered; the
    /// file hash a host records answers the same question exactly.
    /// </summary>
    public string? Version { get; set; }

    public Dictionary<string, object?> Variables { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The resources this sequence needs, by alias, for a station to bind to concrete hardware.
    ///
    /// This is the way forward: a sequence that declares requirements runs unchanged on every
    /// station that can satisfy them. <see cref="Instruments"/>, <see cref="Transports"/> and
    /// <see cref="Services"/> still carry addresses inline and still work, so existing sequences
    /// keep running - but a sequence written that way is pinned to one bench's wiring.
    /// </summary>
    public List<ResourceRequirement> Requires { get; set; } = [];

    public List<InstrumentDefinition> Instruments { get; set; } = [];

    public List<TransportDefinition> Transports { get; set; } = [];

    public List<TestServiceDefinition> Services { get; set; } = [];

    public List<TestItemDefinition> Items { get; set; } = [];

    /// <summary>
    /// Steps that run once the items are done, however they ended: after the last item, after a
    /// step's <c>Stop</c> policy, after a flow definition that could not be evaluated, and after
    /// the operator cancelled. An item's own <c>cleanup</c> only restores what that item touched;
    /// "open the contactor, stop the charger" belongs to the whole test, and before this it had to
    /// be copied into every item that might be the one the run stops at. A called sequence's run at
    /// the end of the call, in its own scope, as the call's last child. Every step runs whatever its
    /// <c>onError</c>, and a cancellation during them runs the rest in the grace window.
    ///
    /// They are skipped when a step was quarantined, as an item's cleanup is - the abandoned
    /// plugin may still be using the resources - and each is then recorded as skipped with that
    /// reason. An error or a failure in them is the sequence's: a DUT left charging is not a pass.
    /// </summary>
    public List<TestStepDefinition> CleanupSteps { get; set; } = [];

    /// <summary>
    /// The item id under which the sequence's <see cref="CleanupSteps"/> are reported - to the
    /// observer, in <c>TestSequenceRunResult.Cleanup</c> and in each step's execution context -
    /// so a host's progress view shows them as one more node without knowing they exist. The
    /// validator refuses an item that uses it.
    /// </summary>
    public const string CleanupItemId = "$cleanup";
}
