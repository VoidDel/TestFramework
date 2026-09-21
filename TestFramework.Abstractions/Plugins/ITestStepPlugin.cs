using TestFramework.Abstractions.Execution;

namespace TestFramework.Abstractions.Plugins;

public interface ITestStepPlugin
{
    // One instance per registered plugin, reused by every step that names it and by every run.
    // A single TestSequenceRunner executes its own steps one at a time, and refuses to start a run
    // while a previous run's plugin has not exited - but only a runner that the host keeps across
    // runs can refuse that, and nothing above it serializes anything. A host driving several
    // runners at once must supply thread-safe plugins. See TestSequenceRunner.PendingStepsCompletion
    // for the protocol a host owes a plugin instance that has not yet exited.
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

    object CreateDefaultSettings();

    object LoadSettings(IReadOnlyDictionary<string, object?> parameters);

    IReadOnlyDictionary<string, object?> SaveSettings(object settings);

    Task<TestStepResult> ExecuteAsync(
        TestStepExecutionContext context,
        object settings,
        CancellationToken cancellationToken);
}
