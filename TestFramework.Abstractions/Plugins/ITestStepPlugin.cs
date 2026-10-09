using TestFramework.Abstractions.Execution;

namespace TestFramework.Abstractions.Plugins;

public interface ITestStepPlugin
{
    // One instance per registered plugin, reused by every step that names it and by every run.
    // A single TestSequenceRunner executes its own steps one at a time, and refuses to start a run
    // while a previous run's plugin has not exited - but only a runner that the host keeps across
    // runs can refuse that. Across runners - one per station, running at once - calls into a plugin
    // that does not declare IsThreadSafe are serialised process-wide, including a call still running
    // after its runner abandoned it. See TestSequenceRunner.PendingStepsCompletion for the protocol a
    // host owes a plugin instance that has not yet exited.
    TestStepPluginDescriptor Descriptor { get; }

    Type SettingsType { get; }

    /// <summary>
    /// What parameters this step takes, for the validator to check and for a host to build an
    /// editor from.
    ///
    /// Empty - the default - means the plugin declares nothing, and everything behaves as it did
    /// before this member existed: the validator leaves the step's parameters alone and a host
    /// without a settings editor plugin falls back to raw key/value text. Declaring parameters is
    /// therefore opt-in, which is what lets this be added inside contract 1.x rather than costing
    /// a major version.
    ///
    /// The list must agree with <see cref="LoadSettings"/>: these are the keys it reads, and the
    /// defaults it applies when a key is absent.
    /// </summary>
    IReadOnlyList<StepParameterDescriptor> Parameters => [];

    /// <summary>
    /// Whether this instance may run for two stations at once.
    ///
    /// False - the default - is the safe answer for a plugin nobody has thought about: the runner
    /// then serialises every call into it across every runner in the process, so several stations
    /// can share one host and one plugin without the plugin having been written for it. A plugin
    /// holding no state between calls, or guarding its own, returns true and runs concurrently.
    /// Added in framework contract 1.1; a plugin built against 1.0 gets the default.
    /// </summary>
    bool IsThreadSafe => false;

    object CreateDefaultSettings();

    object LoadSettings(IReadOnlyDictionary<string, object?> parameters);

    IReadOnlyDictionary<string, object?> SaveSettings(object settings);

    Task<TestStepResult> ExecuteAsync(
        TestStepExecutionContext context,
        object settings,
        CancellationToken cancellationToken);
}
