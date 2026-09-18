# Test Framework Architecture

## Projects

- `TestFramework.Abstractions`: sequence models, execution result models, and plugin contracts.
- `TestFramework.Core`: plugin registry, plugin loader, and sequence runner.
- `TestFramework.SequenceYaml`: YAML load/save plus sequence validation.
- `TestFramework.Plugin.Abstractions.UI`: Avalonia settings editor contract for step plugins.
- `TestFramework.Plugins.BasicSteps`: the built-in step plugins, built as a plugin package rather
  than as part of any host.
- `TestFramework.Plugins.BasicSteps.UI`: the settings editors for those plugins.
- `TestFramework.App`: Avalonia sequence editor and runner shell.

`Abstractions`, `Plugin.Abstractions.UI`, `Core` and `SequenceYaml` are the shipped libraries and
are the only packable projects. Hosts reference `Core` and `SequenceYaml`; plugin repositories
reference only `Abstractions`, plus `Plugin.Abstractions.UI` when they supply a settings editor.

A plugin is never referenced by a host. `TestFramework.App` depends on the built-in plugins only
through `ReferenceOutputAssembly="false"`, which builds them without putting them on the compile
references and stages the output into `Plugins/BasicSteps`. Nothing in the app can name a plugin
type, so the built-in steps travel exactly the path a third-party plugin does - the only way that
path stays honest. See `docs/plugin-development.md` for the plugin repository layout.

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

Runtime step plugins should not depend on Avalonia: a headless or CI host loads them to execute
sequences and has no UI to show. A settings editor is therefore a separate plugin kind,
`IStepSettingsEditorPlugin`, which names the plugin id and version it serves instead of being
implemented by the plugin. It conventionally ships as `MyPlugin.UI.dll` beside `MyPlugin.dll`, and a
plugin that does not care about headless hosts can still implement it on the plugin type itself.

Editors are Avalonia types, so Core cannot discover them. They are found in the same directory scan
through `IPluginTypeHandler`, which lets a host layer contribute its own plugin kind; a second pass
would reload every assembly and hand the host a different copy of each type. Editors are matched to
steps with the same version rule the runner uses, so a step never loses its configuration UI to a
version that resolved forward.

## Framework Contract Version

`FrameworkContract.Version` is the version of the plugin-facing surface this build exposes. It is
separate from the package version: packages ship on their own cadence, while this number moves only
when the surface plugins compile against changes.

A plugin assembly declares the lowest contract it needs with
`[assembly: TestFrameworkPlugin("1.0")]`. The loader reads that declaration as metadata, before
constructing anything, and refuses an assembly needing more than this build provides - reading it
without instantiating the attribute matters precisely because the assembly that must be refused is
the one built against a contract this build may not be able to construct. An assembly declaring
nothing is treated as `FrameworkContract.Baseline`, which is what a plugin predating the attribute
targeted. The declared version is kept in the load report so a host can report what a plugin was
built against and adapt optional calls to it.

The contract is additive-only, so a newer framework keeps running older plugins. That is a
constraint on how it may change: every member added to an existing plugin-facing interface carries a
default implementation, and existing members never change signature or meaning. A change that cannot
be made that way is a new major contract version, and the plugins it locks out are refused at load
time with a message rather than failing somewhere inside a run.

## Plugin Lifecycle and Trust Boundary

Plugins are discovered by scanning the plugin directory for managed assemblies. Each assembly is
loaded into its own collectible `AssemblyLoadContext`, but a context whose types have been
registered stays rooted for the lifetime of the process:

- **Plugins cannot be unloaded.** Replacing or updating a plugin DLL requires restarting the
  application; the file also stays locked while the process runs.
- **Plugin versions resolve exact-first, then forward within the major version.** A step
  referencing `pluginVersion: 1.0.0` runs `1.0.0` when it is installed. When it is not, the highest
  installed `1.x.y` above it is used and the substitution is reported - as a validation warning in
  the editor and a line in the run log - because the sequence file no longer names the code that
  produced the results. A different major version and an older build are both refused: either can
  change behaviour the sequence depends on, and measuring the wrong thing silently is worse than
  refusing to run. `PluginVersionPolicy` is the single implementation of this rule, shared by the
  step registry, the resource registry and the settings-editor registry, so the three cannot drift
  and a running step can never lose its configuration UI to a version mismatch.
- `TestFramework.Abstractions`, `TestFramework.Plugin.Abstractions.UI` and every `Avalonia.*`
  assembly are shared with the host rather than loaded per plugin, so plugins must build against
  the host's versions of them; copies sitting in the plugin folder (which `dotnet publish` produces)
  are ignored. Avalonia has to be shared because the UI contract's `CreateEditor` returns an Avalonia
  `Control`: a privately loaded Avalonia would give that type a second identity and the editor
  assembly would fail to load as not implementing the interface.
  Every other dependency is resolved inside the plugin's own context - except that a
  dependency which is itself a file in the plugin directory is loaded once for the whole process,
  through the same catalog the directory scan uses. A settings-editor assembly depends on the step
  assembly it edits; a private copy of that step assembly would give its settings types a second
  identity, and the editor's cast of the settings object the step plugin created would fail.
- Loading failures are isolated per assembly and per type: one broken plugin type does not discard
  the other plugins in the same assembly, and partially loadable assemblies contribute the types
  that did load. Failures are reported in the load report rather than thrown.
- Native DLLs and managed assemblies containing no plugin types are skipped silently.
- `PluginDirectoryLoader` walks the directory once for both plugin kinds. Step plugins and resource
  plugins share the same files, so a per-kind scan would parse every DLL header and reflect over
  every assembly twice, and - unable to tell a resource-only assembly from an empty one - would
  unload it and immediately load it again. Whether an assembly contributes nothing is only knowable
  after every kind has been scanned, which is also the only point at which it is safe to unload.

## Resource Plugin Contract

Resource plugins (`IInstrumentDriverPlugin`, `ITransportPlugin`, `ITestServicePlugin`) receive an
`IResourceScope` while they build. A driver often needs a resource opened before it - a service
reaching for its transport - so the scope exposes `Instruments`, `Transports` and `Services` for
lookup. It deliberately exposes nothing else: registering and disposing belong to the host, which
owns the container shared by every resource in the run. The concrete container,
`RuntimeResourceProvider`, lives in `TestFramework.Core` rather than beside the contracts, so the
contract package cannot hand a plugin something it is not meant to mutate.

**Trust boundary:** plugin assemblies are loaded without signature or hash verification, and plugin
code runs in-process with full host privileges. Write access to the plugin directory is therefore
equivalent to code execution as the user running the application. The directory must be protected
accordingly in a deployed system. The read-only scope prevents accidental misuse of host resources;
it is not a sandbox.

## Execution Rules

On step exception or timeout, the runner creates an `Error` result and applies the step policy:

- `Stop`: run the current item's cleanup steps, then stop the sequence.
- `Continue`: continue with the next step in the same section.
- `JumpToCleanup`: skip the rest of the current item and run cleanup.

Cleanup steps run when normal execution completes and after both `Stop` and `JumpToCleanup` errors, provided the preceding plugin has exited. They also run after user cancellation, so the device under test is restored rather than left in whatever state the interrupted step produced. Cancellation cleanup runs on a fresh token bounded by `TestSequenceRunner.CleanupGracePeriod` (30 seconds by default), because the user's token is already cancelled and would abort every cleanup step immediately. A cleanup step that outlives the grace period is abandoned and the run still reports `Cancelled`. Cleanup is suppressed entirely when a previous step was quarantined, since its resources may still be in use.

Step execution (including synchronous plugin code and settings loading) runs off the UI thread. The host bounds its wait by the step timeout and user cancellation. A plugin that exits more than 100 ms after cancellation is quarantined: the sequence stops without executing further steps or cleanup against the same resources. `HasPendingExecution` records this condition in the result. A host using `TestSequenceRunner` directly must await `PendingStepsCompletion` before releasing resources or reusing plugin instances. The desktop `SequenceRunService` does this automatically and blocks new runs while recovery is pending. Resource cleanup attempts every resource and aggregates failures; cleanup failures block further desktop runs until the application is restarted.

In-process .NET plugins cannot be forcibly terminated safely. Quarantine prevents concurrent reuse, but does not stop native calls or hardware commands already in progress. Hard termination requires a separate plugin worker process, which is outside the current execution model.

Cancellation throws `TestSequenceCancelledException` (an `OperationCanceledException`) from the core runner. Its `Result` includes completed steps, the interrupted step, variable snapshots, timestamps and a `Cancelled` verdict. The desktop service returns that partial result for JSON persistence. Configured verdict output keys that are absent produce `Error`, never a fallback `Pass`.

## Variables

Variables are sequence-scoped runtime values. `TestSequence.Variables` defines the initial values, and the runner keeps a
case-insensitive mutable variable table for the whole sequence run.

In a sequence file, values under `variables`, `parameters` and `settings` carry their YAML type: an unquoted `5.0` loads
as a double, `7` as an int, `true` as a bool, and a quoted scalar as a string. Saving quotes any string that would read
back as another type (`"007"`, `"true"`) and writes whole doubles with a fraction (`5.0`), so a value keeps its type
across a round trip. Plugins therefore receive numbers as numbers; they should still accept the string form, which is
what a hand-written file or a text field can carry.

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
