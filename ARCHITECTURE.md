# Test Framework Architecture

## Projects

- `TestFramework.Abstractions`: sequence models, execution result models, and plugin contracts.
- `TestFramework.Core`: plugin registry, plugin loader, and sequence runner.
- `TestFramework.SequenceYaml`: YAML load/save plus sequence validation.
- `TestFramework.Plugin.Abstractions.UI`: Avalonia settings editor contract for step plugins.
- `TestFramework.Plugins.BasicSteps`: built-in example step plugins.
- `TestFramework.App`: Avalonia sequence editor and runner shell.

## Sequence Shape

Each test sequence contains test items. Each item has three step sections:

- `init`: initialization steps, optional.
- `main`: primary test steps, required.
- `cleanup`: de-initialization steps, optional.

`verdictSource.stepId` must point to a step in `main`. `verdictSource.outputKey` is optional; when set, the runner converts that output value to a final item verdict.

## Plugin Contract

A step plugin implements `ITestStepPlugin`:

- `CreateDefaultSettings()`
- `LoadSettings(...)`
- `SaveSettings(...)`
- `ExecuteAsync(...)`

Runtime step plugins should not depend on Avalonia. Custom parameter UI is registered in the Avalonia app through
`PluginSettingsEditorRegistry`. For compatibility, an externally loaded plugin that also implements
`ITestStepSettingsEditorProvider` is still registered as a settings editor by the app.

## Execution Rules

On step exception or timeout, the runner creates an `Error` result and applies the step policy:

- `Stop`: run the current item's cleanup steps, then stop the sequence.
- `Continue`: continue with the next step in the same section.
- `JumpToCleanup`: skip the rest of the current item and run cleanup.

Cleanup steps run when normal execution completes and after both `Stop` and `JumpToCleanup` errors, provided the preceding plugin has exited. User cancellation can interrupt cleanup.

Step execution (including synchronous plugin code and settings loading) runs off the UI thread. The host bounds its wait by the step timeout and user cancellation. A plugin that exits more than 100 ms after cancellation is quarantined: the sequence stops without executing further steps or cleanup against the same resources. `HasPendingExecution` records this condition in the result. A host using `TestSequenceRunner` directly must await `PendingStepsCompletion` before releasing resources or reusing plugin instances. The desktop `SequenceRunService` does this automatically and blocks new runs while recovery is pending. Resource cleanup attempts every resource and aggregates failures; cleanup failures block further desktop runs until the application is restarted.

In-process .NET plugins cannot be forcibly terminated safely. Quarantine prevents concurrent reuse, but does not stop native calls or hardware commands already in progress. Hard termination requires a separate plugin worker process, which is outside the current execution model.

Cancellation throws `TestSequenceCancelledException` (an `OperationCanceledException`) from the core runner. Its `Result` includes completed steps, the interrupted step, variable snapshots, timestamps and a `Cancelled` verdict. The desktop service returns that partial result for JSON persistence. Configured verdict output keys that are absent produce `Error`, never a fallback `Pass`.

## Variables

Variables are sequence-scoped runtime values. `TestSequence.Variables` defines the initial values, and the runner keeps a
case-insensitive mutable variable table for the whole sequence run.

Step parameters can reference variables with `${name}`:

- When the whole parameter value is a single placeholder, the original variable type is preserved. For example,
  `Value: ${targetVoltage}` passes a numeric value when `targetVoltage` is numeric.
- When the placeholder is embedded in text, the value is converted to invariant-culture text. For example,
  `Message: DUT-${dutSerial}` becomes a string.
- Variable references are resolved before `ITestStepPlugin.LoadSettings(...)`, so step plugins receive normal settings
  values and do not need to parse variable syntax themselves.
- Undefined references produce a step error. Invalid explicit numeric values in built-in steps also produce an error instead of using defaults. The settings editor uses default preview values for bindings while preserving the original binding text.

Steps can write variables after execution through `variableWrites`:

- `outputKey` copies a value from `TestStepResult.Outputs`.
- `value` writes a configured literal or variable-expanded value.
- Writes are skipped for `Error` results unless `writeOnError` is true.
- A missing configured output is a step error. Variable writes do not run for interrupted steps.

The runner exposes variable snapshots for reporting and other consumers:

- `TestSequenceRunResult.InitialVariables`
- `TestSequenceRunResult.FinalVariables`
- `TestItemRunResult.VariablesAfter`
- `TestStepResult.WrittenVariables`

## Runtime Resources

Steps should depend on named runtime capabilities instead of concrete hardware, protocol, or algorithm implementations.
The sequence can declare:

- `instruments`: hardware capabilities such as power supplies, DMMs, CAN adapters, or serial devices. Simple SCPI
  instruments can be modeled here directly without a separate transport.
- `transports`: optional communication transports, such as raw CAN, ISO-TP, TCP, or serial sessions. A transport may be
  built on an instrument channel, but it can also be standalone, such as a TCP client configured by host and port.
- `services`: optional higher-level capabilities, such as UDS clients, XCP clients, Modbus clients, SCPI sessions,
  parsers, or algorithms. A service may use a transport, but pure algorithm/parser services do not need one.

Step code accesses these through `TestStepExecutionContext.Instruments`, `Transports`, and `Services`. For example, a UDS
step should ask for an `IUdsClient` service alias instead of directly using a Vector or PEAK CAN driver. This keeps the
step stable when the underlying hardware vendor or protocol implementation changes.
