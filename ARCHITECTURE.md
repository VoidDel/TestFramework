# Test Framework Architecture

## Projects

- `TestFramework.Abstractions`: sequence models, execution result models, and plugin contracts.
- `TestFramework.Core`: plugin registry, plugin loader, and sequence runner.
- `TestFramework.SequenceYaml`: YAML load/save plus sequence validation.
- `TestFramework.Plugin.Abstractions.UI`: Avalonia settings editor contract for step plugins.
- `TestFramework.Plugins.BasicSteps`: the built-in step plugins, built as a plugin package rather
  than as part of any host.
- `TestFramework.Plugins.BasicSteps.UI`: the settings editors for those plugins.

`Abstractions`, `Plugin.Abstractions.UI`, `Core` and `SequenceYaml` are the shipped libraries and
are the only packable projects. Hosts reference `Core` and `SequenceYaml`; plugin repositories
reference only `Abstractions`, plus `Plugin.Abstractions.UI` when they supply a settings editor.

**There is no host in this repository.** A host is an application with its own release cadence,
its own operators and its own opinions about layout; keeping one here made the framework's shape
follow whatever that one application happened to need. The reference host is the `BMS-TEST`
repository, checked out beside this one, which consumes these four packages from a folder feed - so
it can only use what they publish, and a private change here breaks its build rather than going
unnoticed.

A plugin is never referenced by a host either. The built-in steps are not packed: they are staged
as files into `artifacts/plugins/BasicSteps`, which a host copies into its own `Plugins/`
directory, and the release workflow zips the same folder. Nothing in a host can name a plugin
type, so the built-in steps travel exactly the path a third-party plugin does - the only way that
path stays honest. See `docs/plugin-development.md` for the plugin repository layout.

## Sequence Shape

Each test sequence contains a tree of items: test items, groups of items, and calls into other
sequences (see Flow Control). Each test item has three step sections:

- `init`: initialization steps, optional.
- `main`: primary test steps, required.
- `cleanup`: de-initialization steps, optional.

`verdictSource.stepId` must point to a step in `main`. `verdictSource.outputKey` is optional; when set, the runner converts that output value to a final item verdict.

An item may also list `checks`: further judgments, each of one main step's output and each with its
own limits, unit and comparison - voltage and current in one item, each against its own range. A
check is written exactly like `verdictSource`, plus an optional `name` for its records, and it always
needs an `outputKey`, since taking a step's own verdict is what the verdict source is for. They sit
in a list beside `verdictSource` rather than replacing it because plugins read
`TestItemDefinition` through the execution context, and an existing member cannot change.

The judgments - the verdict source and every check - decide the item together, but they cannot
overrule the rest of it. An item is `Error` when any of its steps errored or any judgment could not
be made, and otherwise `Fail` when any judgment failed or any step returned `Fail` - in `init`,
`main` or `cleanup`. Only when every judgment passed does the item pass; one that could not judge
its value leaves the item `Inconclusive`. Two limit checks in one item is the natural way to write
"voltage and current", and letting the second one's `Pass` hide the first one's `Fail` would ship a
failing unit as good. The one exception is a step whose output a judgment reads: judging that output
replaces the step's own verdict.

## Plugin Contract

A step plugin implements `ITestStepPlugin`:

- `Parameters` — optional, declares what the step takes.
- `CreateDefaultSettings()`
- `LoadSettings(...)`
- `SaveSettings(...)`
- `ExecuteAsync(...)`

### Declared Parameters

`Parameters` returns a `StepParameterDescriptor` per parameter: name, kind, default, range or
choices, whether it is required, and whether a `${variable}` may stand in for it. It has a default
implementation returning nothing, so plugins written before it existed keep working unchanged and
the contract stays 1.x.

Declaring parameters buys two things. `TestSequenceValidator` checks the step's values against the
declaration before a run starts, so a wrong type or an out-of-range number is reported while the
sequence is being edited instead of part-way through a run with a DUT connected. And a host can
build the settings form from the declaration, so a plugin only ships a settings editor when its
parameters genuinely need a custom control.

A plugin that declares nothing is left entirely alone: the validator does not check its parameters
and a host falls back to a raw key/value list, exactly as before.

`StepParameterCheck` implements the value rules once and is used by both the validator and the
host's generated form, so a value a form accepts cannot be one the validator later refuses.

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

Contract **1.1** (framework 0.5.0) added, all additively: `ITestStepPlugin.IsThreadSafe` with a
default of `false`, `StepParameterKind.Expression`, and `TestStepExecutionContext.Operator`. A 1.0
plugin runs unchanged. A plugin that uses any of them declares 1.1, so a 1.0 host refuses it at load
with a reason instead of failing on a missing member mid-run.

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
contract package cannot hand a plugin something it is not meant to mutate. Plugins never receive
the container itself either - it is also the disposable owner of every resource, and a cast to
`IDisposable` would let one plugin release the others' resources. Resource plugins and step
contexts get `ReadOnlyResourceScope`, which implements the lookup interfaces and nothing else.

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

Step execution (including synchronous plugin code and settings loading) runs off the UI thread. The host bounds its wait by the step timeout and user cancellation. A plugin that exits more than 100 ms after cancellation is quarantined: the sequence stops without executing further steps or cleanup against the same resources. `HasPendingExecution` records this condition in the result. A host using `TestSequenceRunner` directly must await `PendingStepsCompletion` before releasing resources or reusing plugin instances, and must keep the resources alive until it completes. Resource cleanup attempts every resource and aggregates failures. A host is expected to surface a cleanup failure and stop accepting runs: the device state is unknown, and the next run would measure against it.

`TestRunSession` (see Run Orchestration) applies every rule in this paragraph and the next; a host
that drives `TestSequenceRunner` directly owes them itself.

That gate is per-runner, so **a host keeps one `TestSequenceRunner` for the life of the bench** and
passes each run's scope to `RunAsync(sequence, resources, cancellationToken)`. Supplying resources
through the constructor instead forces a new runner whenever the scope changes - which a station
scope does every run - and a new runner knows nothing about the plugin the previous one abandoned.
It would resolve the same singleton plugin instance out of the registry and call `ExecuteAsync` on
it while the first call is still driving the hardware. The constructor argument remains for a host
whose resources never change across runs.

In-process .NET plugins cannot be forcibly terminated safely. Quarantine prevents concurrent reuse, but does not stop native calls or hardware commands already in progress. Hard termination requires a separate plugin worker process, which is outside the current execution model.

Cancellation throws `TestSequenceCancelledException` (an `OperationCanceledException`) from the core runner. Its `Result` includes completed steps, the interrupted step, variable snapshots, timestamps and a `Cancelled` verdict - a host should persist it rather than discard it, because it is the record of what the device under test was actually asked to do. Configured verdict output keys that are absent produce `Error`, never a fallback `Pass`.

### Numeric Verdicts and Units

A numeric verdict compares a step output against `lowerLimit` and `upperLimit`. Which scale that
comparison happens in is decided by two fields, and the rule is worth stating precisely because
getting it wrong means passing a DUT that should have failed:

- `unit` is **the unit the limits are written in** - the conversion target. Leaving it empty asks
  for no conversion at all, and the limits are then read in whatever unit the measurement itself is
  in. That is a supported configuration, not an oversight: a plugin whose readings are always in mV
  pairs with limits written in mV.
- `sourceUnit` is the measurement's unit. It applies **only when the measured value does not carry
  one itself**.
- A value that carries its own unit (`"1234 mV"`) overrides `sourceUnit`. The instrument knows what
  it returned better than the sequence file does.
- When `unit` is declared and differs from the measurement's unit, both must be units this build
  knows and both must be of the same dimension. An unrecognised unit, or `mV` against `ms`,
  produces `Inconclusive` - the comparison is never made on raw numbers as a fallback, because a
  silent comparison in the wrong scale is exactly the failure this resolution exists to prevent.
  A value that is not a number at all is `Inconclusive` for the same reason.

`Ω` normalises to `Ohm`, and both `µ` (U+00B5 MICRO SIGN) and `μ` (U+03BC GREEK SMALL LETTER MU)
normalise to `u`, since instruments and operators produce either one.

#### Comparisons

`comparison` says how the value is compared, using TestStand's codes so a sequence reads the same to
anyone who has written one there: `GELE` (the default), `GTLT`, `GELT`, `GTLE`, `GT`, `GE`, `LT`,
`LE`, `EQ` and `NE`. The interval codes read both limits, `GT`/`GE` only `lowerLimit`, `LT`/`LE`
only `upperLimit`, and `EQ`/`NE` read `expected`, which takes a number or a `${variable}` like the
limits do. `NumericComparisonRules` is the one statement of which code reads what, shared by the
validator, the runner and a host's editor.

`GELE` keeps exactly what a numeric verdict did before comparisons existed, including a single
limit standing alone as a one-sided test. Every other code needs every bound it reads: someone who
asked for an open interval and wrote one bound has made a mistake, and the single-limit codes exist
for a one-sided test. A missing bound is an item `Error` at run time and an error in the validator;
a bound the comparison does not read is a validator warning and is ignored at run time - it is
usually left over from switching comparison, and a reader will take it as part of the test.

`EQ` and `NE` compare exactly. They are for integral values - a cell count, a status code - and the
validator warns about a fractional `expected`, which a measured value will not land on after any
conversion or instrument rounding; a `GELE` tolerance band is what was meant.

The keys added with comparisons and checks - `name`, `comparison`, `expected`, `checks` - are not
written while they hold their defaults, so a sequence that does not use them saves byte for byte as
it did before, and a file under version control does not change on its first save after an upgrade.

#### Multi-channel outputs

A numeric verdict on a list or dictionary output judges **every element** against the same limits.
A step that reads 80 cell voltages in one transaction is therefore one item, not 80: any element
outside the limits fails the item, any element that cannot be read as a number (and none failing)
leaves it `Inconclusive`, and an output with no elements is `Inconclusive` too - it measured
nothing, and passing it would pass a DUT on no evidence. A dictionary's keys, usually signal names,
name the elements; a list's elements are named after the judgment - its `name`, or its `outputKey`
when it has none - as `name[0]`, `name[1]` and so on. Each judgment combines only its own elements,
so a check whose value could not be read is not outvoted by the verdict source's passing ones.

#### Limits from variables

`lowerLimit`, `upperLimit` and `expected` take either a number or a single `${variable}`, so the limits for a
product variant live in its variables rather than in every item. A reference is resolved when the
item is judged - after its steps have run - so a step may compute a limit and write it. Because
plugins read `VerdictSource` through the execution context, the model keeps the reference in
`LowerLimitReference` / `UpperLimitReference` rather than changing the type of `LowerLimit`; in a
file both are the same key. The validator checks a reference the way it checks a parameter's: it
must be the whole value, the variable must exist by then, and a value written in the file must be a
number. At run time an undefined or non-numeric limit, no limit at all, or a lower limit above the
upper one makes the item `Error` with `TestItemRunResult.ErrorMessage` saying why - a fault in the
sequence, not in the DUT, and one that would otherwise drop a bound out of the comparison or fail
every unit.

#### What a result records

`TestItemRunResult.Measurements` holds one `MeasurementResult` per judged value, across the verdict
source and every check - the judgment it came from (`Check`), name, index, the raw value, the
number compared and its unit, the comparison, the limits or expected value as resolved, and the
element's verdict. The limits are recorded as they stood at judgment time instead of being left for
a report to look up in the sequence, which can be edited after the run and whose limits can come
from variables. Every judgment is made and recorded even when a step has already failed the item: a
spread check failing must not cost the report the answer to which cell was low.
`TestStepResult.PluginId` and `PluginVersion` name the plugin that actually ran, including on an
error result; when a compatible newer version stood in, the version is the one that ran, not the one
the file asked for.

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
- `$${` writes a literal `${`: `$${targetVoltage}` reaches the plugin as the nine characters
  `${targetVoltage}`. The escape is `$$` **only directly before a brace**, so `$$` anywhere else is
  two dollar signs and is left alone.
- Text that holds a `${` which is neither a reference nor an escape - `${1stReading}`,
  `${my var}`, a missing closing brace - is reported as a warning. The resolver passes it through as
  literal text, so nothing downstream would ever object, and those near-misses are exactly the shape
  a mistyped variable name takes.

A reference does not switch off the parameter's type check. Where the variable's value is knowable
before the run, it is checked against the declared kind exactly as a literal would be:

- Knowable means the value is written in the file and nothing reassigns it. A variable that any
  `variableWrites` targets is treated as defined with an unknown type, because what a plugin output
  carries is the plugin's business and whether a given write executes depends on enablement and on
  error policies.
- Only the type is checked, never the range: a limit belongs to the value a run produces, and an
  initial value is not that.
- Only a reference that is the whole value is checked. Embedded in text the result is text whatever
  the variable holds.

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

## Stations and Resource Scopes

A sequence declaring `instruments` inline carries its addresses with it, which pins it to one
bench: five stations with different ports need five copies of the sequence, or the station's wiring
pasted into each one.

`requires` is the alternative. A sequence declares what it needs — an alias, a resource kind, and
optionally a driver or minimum version — and the station says what is behind each alias:

- `TestSequence.Requires` — `ResourceRequirement`: "this test needs a supply I call `psu`".
- `StationConfiguration` — `StationResourceBinding`: "on this bench `psu` is a Keysight on COM7".

`StationBinding.Check` matches the two, and is used by both `TestSequenceValidator` and the runtime,
so "this sequence can run here" means the same thing before a run and during one. A bench that does
not bind a required alias is an error at validation; with no station configured at all it is a
warning, because a host cannot answer the question for a bench it is not.

### Scopes

`StationResourceHost` owns two nested scopes:

- The **station scope** opens bindings marked `shared` (the default) once and keeps them open across
  runs. Opening a USB-CAN adapter per DUT costs about a second each time, some instruments need to
  warm up, and some drivers fail when cycled.
- The **run scope** nests inside it, holding the sequence's own inline resources and any binding
  marked `shared: false`. It is released when the run ends; the station's resources are not. A
  lookup that misses in the run scope falls through to the station scope, so a step asks for `psu`
  without knowing which scope opened it. A run resource shadows a station resource of the same
  alias.

After a run that ends in `TestVerdict.Error`, `TestRunSession` marks the station scope stale and the
next run reopens it. A `Fail` — a limit check that came out low — says nothing about the hardware
and costs no reconnection, but anything that threw left a step part-way through, possibly
mid-transaction on an instrument, and the next DUT must not inherit that. Nothing is torn down at
the moment of the fault: the operator may still be looking at it, so the rebuild happens at the
start of the next run where it is expected and can be reported.

## Run Orchestration

`TestSequenceRunner` runs a sequence; it does not decide when a bench may run the next one. Those
decisions used to be every host's to re-derive - the reference host had about forty lines of them,
correct, and easy to get wrong silently in a second host. `TestRunSession` makes them once:

- It holds **one runner for the life of the bench**, so the runner's quarantine gate spans runs.
- It opens each run's scope - from the `StationResourceHost`, or from the sequence's inline
  definitions when there is no station - and marks the station stale after an `Error`.
- When a step was abandoned it keeps that run's resources until the plugin exits, and **refuses the
  next run** until then, before opening anything for it.
- When releasing resources fails it records a **fault** and refuses runs until the host calls
  `ClearFault`, which it should do only once someone has checked the hardware: the device state is
  unknown and the next run would measure against it.
- A second run while one is in progress is refused rather than queued, because a queued run starts
  a DUT the operator believed had been turned away.
- Every refusal is a `TestRunRefusedException` with a `TestRunRefusal` reason, so a host words it
  for its own operators; every run that starts returns a result, cancelled or not.

## Flow Control

An item is one of three kinds: a test item with steps, a **group** whose `items` run in order and
whose verdict is theirs, or a **call** that runs another sequence's items in its place. A group
or a call has no steps and no judgments of its own; the validator rejects either. The tree runs
depth first and shares one variable scope, except that a called sequence runs in a scope of its
own - its variables, overridden by the call's `parameters` resolved in the caller's scope - and
nothing it writes leaks back. It uses the caller's resources: the station is the caller's. An
`ISequenceResolver` supplied by the host finds called sequences; `FileSequenceResolver` resolves
paths under one directory and refuses any that leave it, because a call is part of a reviewed test
and must not reach a file nobody reviewed with it. Calls nest at most `MaxCallDepth` (16) deep,
which is how a sequence calling itself ends.

On top of the kind, any item - and `runIf`/`retry` on any step - can carry:

- `runIf`: a condition evaluated just before the node would run; false records it `Skipped` with a
  `SkipReason`.
- `loop` (items): `count` iterations, a whole number from an expression, with the index in
  `variable`. Each iteration is its own result carrying `Iteration`.
- `retry`: `maxAttempts` in total, `intervalMs` between them, and an optional `until` condition
  evaluated after each attempt's variable writes. A step is retried on `Error` or `Fail`; an item
  only on `Fail`, because an item that errored left a step part-way through and rerunning it
  against hardware in that state spreads the fault. A quarantined step is never retried - its
  plugin is still running. A poll that runs out of attempts with `until` still false is a `Fail`.
  Failed attempts are kept on the final result (`PreviousAttempts`); a step's polls that merely
  found their condition not yet true are not, or a ten-minute poll would carry six hundred of them.

A flow definition that cannot be evaluated - a `runIf` that is not a boolean, a count that is not a
number, a call that cannot be resolved - stops the sequence with an `Error` naming the expression.
Skipping would pass by default and running would ignore the author's condition; neither should
happen silently.

### Expressions

Conditions, counts, `until` and `basic.calculate` share one small language, `SequenceExpression`:
arithmetic, comparison and logic, list indexing, and `abs`, `round`, `min`, `max`, `sum`, `avg`,
`count`. Variables are written `${name}`, as everywhere else, so the validator recognises a
reference the same way in an expression as in a parameter, and checks that every one it reads
exists where it is evaluated. The language is strict on purpose: a condition must be a boolean,
never truthy; text compared with a number, a division by zero or an undefined variable is an error
rather than a guess. All numbers are doubles, and text that reads as a number compares as one,
since a value typed into a run parameter arrives as text.

A step parameter declared `StepParameterKind.Expression` is passed to its plugin unsubstituted -
its `${}` are operands, and replacing a list variable with its text would destroy them - and the
plugin evaluates it against the execution context's variables.

## Concurrency Across Stations

A step plugin is one instance shared by every runner in the process. Several stations in one host
are several runners calling it at once, and most plugins were written for one bench. So unless a
plugin declares `IsThreadSafe`, every call into it is serialised process-wide (`PluginExecutionGate`,
keyed by the instance). The gate is released when the plugin actually returns, not when a runner
stops waiting for it, so an abandoned call keeps other stations out of a plugin that is still
driving hardware. Time spent waiting counts against the step's timeout, and a step that times out
in the queue says so rather than blaming the plugin. The built-in steps hold no state and are
declared thread-safe.

## Results and Traceability

A host passes a `TestRunInfo` - DUT serial number, operator, station, sequence file path and hash,
free-form properties - and the result carries a copy, together with the sequence's own `version`,
the framework build and its contract version. Each step result names the plugin and the version
that actually ran beside the version the file requested, and each measurement records the
comparison and the limits as resolved. A result therefore answers on its own which unit, on which
bench, by whom, against which bytes of which test, by which code.

`TestResultJson` is the framework's result file format: an envelope (`format`, `schemaVersion`,
`result`) so a reader can refuse what it does not understand, camelCase properties, enums by name,
exceptions as type, message, stack and inner exceptions (read back as `RecordedException`), and
values as JSON can carry them - with anything else written as its invariant text, because a result
must always be writable. One format means one report generator and one archive for every bench.
