using System.Collections.Concurrent;
using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Plugins;
using TestFramework.Core.Execution;
using TestFramework.Core.Plugins;
using TestFramework.SequenceYaml;
using TestFramework.SequenceYaml.Validation;
using Xunit;

namespace TestFramework.Tests;

/// <summary>
/// runIf, retry (with until), loop, groups and calls; what a result records about the run; and
/// plugins shared by runners running at once.
/// </summary>
public sealed class FlowControlTests
{
    [Fact]
    public async Task StepRetry_RunsAgainUntilItPasses_AndKeepsTheFailedAttempts()
    {
        var plugin = new ScriptPlugin();
        var step = Step("read", ("verdicts", "Fail,Error,Pass"));
        step.Retry = new RetryDefinition { MaxAttempts = 3 };

        var item = await RunItemAsync(plugin, step);
        var result = Assert.Single(item.MainResults);

        Assert.Equal(TestVerdict.Pass, result.Verdict);
        Assert.Equal(3, result.Attempts);
        Assert.Equal([TestVerdict.Fail, TestVerdict.Error], result.PreviousAttempts.Select(attempt => attempt.Verdict));
        Assert.Equal(TestVerdict.Pass, item.Verdict);
    }

    [Fact]
    public async Task StepRetry_ThatRunsOut_KeepsTheLastVerdict()
    {
        var step = Step("read", ("verdicts", "Fail"));
        step.Retry = new RetryDefinition { MaxAttempts = 2 };

        var result = Assert.Single((await RunItemAsync(new ScriptPlugin(), step)).MainResults);

        Assert.Equal(TestVerdict.Fail, result.Verdict);
        Assert.Equal(2, result.Attempts);
        Assert.Single(result.PreviousAttempts);
    }

    [Fact]
    public async Task StepRetryUntil_PollsUntilTheConditionHolds_WithoutKeepingThePolls()
    {
        // The plugin counts its calls; the step writes the count to ${soc} and polls until it is 3.
        var step = Step("poll");
        step.VariableWrites.Add(new VariableWriteDefinition { Name = "soc", OutputKey = "calls" });
        step.Retry = new RetryDefinition { MaxAttempts = 10, Until = "${soc} >= 3" };

        var result = Assert.Single((await RunItemAsync(new ScriptPlugin(), step)).MainResults);

        Assert.Equal(TestVerdict.Pass, result.Verdict);
        Assert.Equal(3, result.Attempts);
        Assert.Empty(result.PreviousAttempts);
    }

    [Fact]
    public async Task StepRetryUntil_ThatNeverHolds_IsAFail()
    {
        var step = Step("poll");
        step.VariableWrites.Add(new VariableWriteDefinition { Name = "soc", OutputKey = "calls" });
        step.Retry = new RetryDefinition { MaxAttempts = 2, Until = "${soc} >= 95" };

        var item = await RunItemAsync(new ScriptPlugin(), step);
        var result = Assert.Single(item.MainResults);

        Assert.Equal(TestVerdict.Fail, result.Verdict);
        Assert.Contains("still false after 2", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(TestVerdict.Fail, item.Verdict);
    }

    [Theory]
    [InlineData("${mode} == 'charge'", TestVerdict.Pass, 1)]
    [InlineData("${mode} == 'discharge'", TestVerdict.Skipped, 0)]
    public async Task StepRunIf_DecidesWhetherTheStepRuns(string condition, TestVerdict verdict, int calls)
    {
        var plugin = new ScriptPlugin();
        var guarded = Step("guarded");
        guarded.RunIf = condition;

        var item = await RunItemAsync(plugin, [Step("first"), guarded], new() { ["mode"] = "charge" });

        Assert.Equal(verdict, item.MainResults[1].Verdict);
        Assert.Equal(calls, plugin.CallsTo("guarded"));
        if (verdict == TestVerdict.Skipped)
        {
            Assert.Contains("discharge", item.MainResults[1].SkipReason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task StepRunIf_ThatCannotBeEvaluated_IsAStepError()
    {
        var plugin = new ScriptPlugin();
        var guarded = Step("guarded");
        guarded.RunIf = "${missing} > 1";

        var item = await RunItemAsync(plugin, [guarded]);

        Assert.Equal(TestVerdict.Error, item.MainResults[0].Verdict);
        Assert.Contains("not defined", item.MainResults[0].ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(0, plugin.CallsTo("guarded"));
    }

    [Fact]
    public async Task ItemRunIf_False_SkipsTheItemWithoutRunningIt()
    {
        var plugin = new ScriptPlugin();
        var item = Item("discharge", Step("read"));
        item.RunIf = "${mode} == 'discharge'";

        var result = await RunAsync(plugin, new TestSequence { Variables = { ["mode"] = "charge" }, Items = [item] });

        var skipped = Assert.Single(result.ItemResults);
        Assert.Equal(TestVerdict.Skipped, skipped.Verdict);
        Assert.NotNull(skipped.SkipReason);
        Assert.Equal(0, plugin.CallsTo("read"));
    }

    [Fact]
    public async Task ItemLoop_RunsOncePerIndex_WithTheIndexInAVariable()
    {
        var plugin = new ScriptPlugin();
        var item = Item("channel", Step("read", ("echo", "${channel}")));
        item.Loop = new LoopDefinition { Count = "${channels}", Variable = "channel" };

        var result = await RunAsync(plugin, new TestSequence { Variables = { ["channels"] = 3 }, Items = [item] });

        Assert.Equal([0, 1, 2], result.ItemResults.Select(iteration => iteration.Iteration));
        Assert.Equal(new object?[] { 0, 1, 2 }, result.ItemResults.Select(iteration => iteration.MainResults[0].Outputs["echo"]));
        Assert.Equal(TestVerdict.Pass, result.Verdict);
    }

    [Fact]
    public async Task ItemLoop_WithAnUnusableCount_StopsTheSequence()
    {
        var plugin = new ScriptPlugin();
        var looped = Item("looped", Step("read"));
        looped.Loop = new LoopDefinition { Count = "'three'" };

        var result = await RunAsync(plugin, new TestSequence { Items = [looped, Item("after", Step("later"))] });

        var error = Assert.Single(result.ItemResults);
        Assert.Equal(TestVerdict.Error, error.Verdict);
        Assert.Contains("loop count", error.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(0, plugin.CallsTo("later"));
    }

    [Fact]
    public async Task ItemRetry_RunsAFailedItemAgain_AndKeepsTheEarlierAttempt()
    {
        // The first reading is out of range, the second in: the item passes on attempt 2, and
        // the failed attempt's measurement is still on record.
        var item = Item("voltage", Step("read", ("values", "5.0,3.3")));
        item.VerdictSource = new VerdictSource
        {
            StepId = "read",
            OutputKey = "value",
            JudgeType = VerdictJudgeType.Numeric,
            LowerLimit = 3.0,
            UpperLimit = 3.6
        };
        item.Retry = new RetryDefinition { MaxAttempts = 3 };

        var result = await RunAsync(new ScriptPlugin(), new TestSequence { Items = [item] });

        var final = Assert.Single(result.ItemResults);
        Assert.Equal(TestVerdict.Pass, final.Verdict);
        Assert.Equal(2, final.Attempt);
        var earlier = Assert.Single(final.PreviousAttempts);
        Assert.Equal(TestVerdict.Fail, earlier.Verdict);
        Assert.Equal(5.0, Assert.Single(earlier.Measurements).Value);
    }

    [Fact]
    public async Task ItemRetry_DoesNotRunAnErroredItemAgain()
    {
        var plugin = new ScriptPlugin();
        var item = Item("broken", Step("read", ("verdicts", "Error,Pass")));
        item.MainSteps[0].OnError = ErrorHandlingMode.Continue;
        item.Retry = new RetryDefinition { MaxAttempts = 3 };

        var result = await RunAsync(plugin, new TestSequence { Items = [item] });

        Assert.Equal(TestVerdict.Error, Assert.Single(result.ItemResults).Verdict);
        Assert.Equal(1, plugin.CallsTo("read"));
    }

    [Fact]
    public async Task Group_IsJudgedByItsChildren()
    {
        var group = new TestItemDefinition
        {
            Id = "charge",
            Name = "充电测试",
            Items = [Item("a", Step("a")), Item("b", Step("b", ("verdicts", "Fail")))]
        };

        var result = await RunAsync(new ScriptPlugin(), new TestSequence { Items = [group, Item("after", Step("after"))] });

        var groupResult = result.ItemResults[0];
        Assert.Equal(TestVerdict.Fail, groupResult.Verdict);
        Assert.Equal(["a", "b"], groupResult.Children.Select(child => child.ItemId));
        Assert.Equal(2, result.ItemResults.Count);
        Assert.Equal(TestVerdict.Fail, result.Verdict);
    }

    [Fact]
    public async Task Group_ChildStoppingTheSequence_StopsEverythingAfterIt()
    {
        var plugin = new ScriptPlugin();
        var group = new TestItemDefinition
        {
            Id = "group",
            Name = "group",
            Items = [Item("bad", Step("bad", ("verdicts", "Error"))), Item("sibling", Step("sibling"))]
        };

        var result = await RunAsync(plugin, new TestSequence { Items = [group, Item("after", Step("after"))] });

        Assert.Equal(TestVerdict.Error, result.ItemResults[0].Verdict);
        Assert.Single(result.ItemResults);
        Assert.Equal(0, plugin.CallsTo("sibling"));
        Assert.Equal(0, plugin.CallsTo("after"));
    }

    [Fact]
    public async Task Call_RunsTheCalledSequenceInItsOwnScope()
    {
        var plugin = new ScriptPlugin();
        var callee = new TestSequence
        {
            Id = "insulation",
            Name = "绝缘检查",
            Variables = { ["testVoltage"] = 500, ["label"] = "default" },
            Items = [Item("hipot", WithWrite(Step("hipot", ("echo", "${testVoltage}")), "leaked", "calls"))]
        };
        var resolver = new MapResolver { ["shared/insulation.yaml"] = callee };
        var call = new TestItemDefinition
        {
            Id = "call",
            Name = "调用绝缘检查",
            Call = new SequenceCallDefinition
            {
                Path = "shared/insulation.yaml",
                Parameters = { ["testVoltage"] = "${packVoltage}" }
            }
        };

        var result = await RunAsync(
            plugin,
            new TestSequence { Variables = { ["packVoltage"] = 1000 }, Items = [call] },
            resolver);

        var called = Assert.Single(result.ItemResults);
        Assert.Equal(TestVerdict.Pass, called.Verdict);
        Assert.Equal("insulation", called.CalledSequenceId);
        Assert.Equal("shared/insulation.yaml", called.CalledSequencePath);
        Assert.Equal(1000, Assert.Single(called.Children).MainResults[0].Outputs["echo"]);
        Assert.False(result.FinalVariables.ContainsKey("leaked"));
    }

    [Fact]
    public async Task Call_WithoutAResolver_IsAnErrorThatSaysSo()
    {
        var call = new TestItemDefinition { Id = "call", Name = "call", Call = new SequenceCallDefinition { Path = "x.yaml" } };

        var result = await RunAsync(new ScriptPlugin(), new TestSequence { Items = [call] });

        Assert.Equal(TestVerdict.Error, result.Verdict);
        Assert.Contains("no sequence resolver", result.ItemResults[0].ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Call_ToItself_EndsAtTheDepthLimit()
    {
        var self = new TestSequence { Id = "self", Name = "self" };
        self.Items.Add(new TestItemDefinition { Id = "again", Name = "again", Call = new SequenceCallDefinition { Path = "self" } });

        var result = await RunAsync(new ScriptPlugin(), self, new MapResolver { ["self"] = self });

        Assert.Equal(TestVerdict.Error, result.Verdict);
        var deepest = result.ItemResults[0];
        while (deepest.Children.Count > 0)
        {
            deepest = deepest.Children[0];
        }

        Assert.Contains("nested deeper than", deepest.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Result_CarriesWhatTheRunWasFor()
    {
        var registry = new PluginRegistry();
        registry.Register(new ScriptPlugin(new Version(1, 2, 0)));
        var sequence = new TestSequence { Version = "B04", Items = [Item("item", Step("read"))] };
        var info = new TestRunInfo
        {
            DutSerialNumber = "BMS-0042",
            Operator = "张三",
            StationId = "EOL-3",
            SequenceFilePath = @"config\sequence\pack.yaml",
            SequenceHash = "sha256:abc",
            Properties = { ["workOrder"] = "WO-17" }
        };

        var result = await new TestSequenceRunner(registry).RunAsync(sequence, null, info);

        Assert.Equal("BMS-0042", result.RunInfo.DutSerialNumber);
        Assert.Equal("张三", result.RunInfo.Operator);
        Assert.Equal("EOL-3", result.RunInfo.StationId);
        Assert.Equal("sha256:abc", result.RunInfo.SequenceHash);
        Assert.Equal("WO-17", result.RunInfo.Properties["workOrder"]);
        Assert.Equal("B04", result.SequenceVersion);
        Assert.Equal("1.1", result.FrameworkContractVersion);
        Assert.False(string.IsNullOrWhiteSpace(result.FrameworkVersion));
        var step = result.ItemResults[0].MainResults[0];
        Assert.Equal("1.0.0", step.RequestedPluginVersion);
        Assert.Equal("1.2.0", step.PluginVersion);

        // A copy: the host changing its object afterwards must not rewrite a finished result.
        info.DutSerialNumber = "changed";
        Assert.Equal("BMS-0042", result.RunInfo.DutSerialNumber);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public async Task PluginShared_ByRunnersRunningAtOnce_IsSerialisedUnlessThreadSafe(bool threadSafe, int expectedPeak)
    {
        var plugin = new OverlapPlugin(threadSafe);
        var registry = new PluginRegistry();
        registry.Register(plugin);
        TestSequence Sequence() => new() { Items = [Item("item", new TestStepDefinition { Id = "s", Name = "s", PluginId = "probe.overlap" })] };

        await Task.WhenAll(
            new TestSequenceRunner(registry).RunAsync(Sequence()),
            new TestSequenceRunner(registry).RunAsync(Sequence()));

        Assert.Equal(expectedPeak, plugin.PeakConcurrency);
    }

    [Fact]
    public async Task TimeoutWaitingForASharedPlugin_SaysWhatItWaitedFor()
    {
        var plugin = new OverlapPlugin(threadSafe: false, holdMs: 1000);
        var registry = new PluginRegistry();
        registry.Register(plugin);
        var slow = new TestSequence { Items = [Item("item", new TestStepDefinition { Id = "s", Name = "s", PluginId = "probe.overlap" })] };
        var impatient = new TestSequence
        {
            Items = [Item("item", new TestStepDefinition { Id = "s", Name = "s", PluginId = "probe.overlap", TimeoutMs = 100, OnError = ErrorHandlingMode.Continue })]
        };

        var first = new TestSequenceRunner(registry).RunAsync(slow);
        await plugin.Entered.Task;
        var second = await new TestSequenceRunner(registry).RunAsync(impatient);
        await first;

        Assert.Contains("which another run is using", second.ItemResults[0].MainResults[0].ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Yaml_FlowDefinitions_RoundTrip()
    {
        var yaml = """
            schemaVersion: 1
            id: flow
            name: Flow
            version: B04
            variables:
              channels: 4
            items:
            - id: charge
              name: 充电测试
              runIf: ${channels} > 0
              items:
              - id: per-channel
                name: 单通道
                loop:
                  count: ${channels}
                  variable: channel
                retry:
                  maxAttempts: 2
                verdictSource:
                  stepId: read
                main:
                - id: read
                  name: Read
                  pluginId: probe.script
                  retry:
                    maxAttempts: 600
                    intervalMs: 1000
                    until: ${soc} >= 95
                  runIf: ${channel} != 3
              - id: shared
                name: 绝缘检查
                call:
                  path: shared/insulation.yaml
                  parameters:
                    testVoltage: 500
            """;
        var service = new TestSequenceYamlService();

        var saved = service.Save(service.Load(yaml));
        var sequence = service.Load(saved);
        var group = sequence.Items[0];
        var looped = group.Items[0];
        var step = looped.MainSteps[0];

        Assert.Equal("B04", sequence.Version);
        Assert.Equal("${channels} > 0", group.RunIf);
        Assert.True(group.IsGroup);
        Assert.Equal("${channels}", looped.Loop!.Count);
        Assert.Equal("channel", looped.Loop.Variable);
        Assert.Equal(2, looped.Retry!.MaxAttempts);
        Assert.Equal(600, step.Retry!.MaxAttempts);
        Assert.Equal("${soc} >= 95", step.Retry.Until);
        Assert.Equal("${channel} != 3", step.RunIf);
        Assert.Equal("shared/insulation.yaml", group.Items[1].Call!.Path);
        Assert.Equal(500, group.Items[1].Call!.Parameters["testVoltage"]);

        // A group and a call carry no empty verdict source or step lists of their own.
        var groupBlock = saved[saved.IndexOf("- id: charge", StringComparison.Ordinal)..saved.IndexOf("  - id: per-channel", StringComparison.Ordinal)];
        Assert.DoesNotContain("verdictSource", groupBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("main:", groupBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void Hash_IdentifiesTheExactText()
    {
        var hash = TestSequenceYamlService.ComputeHash("name: a\n");

        Assert.StartsWith("sha256:", hash, StringComparison.Ordinal);
        Assert.Equal(71, hash.Length);
        Assert.Equal(hash, TestSequenceYamlService.ComputeHash("name: a\n"));
        Assert.NotEqual(hash, TestSequenceYamlService.ComputeHash("name:  a\n"));
    }

    [Fact]
    public void FileResolver_RefusesAPathOutsideItsDirectory()
    {
        using var directory = new TempDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "inside.yaml"), "schemaVersion: 1\nid: inside\nname: Inside\n");
        var resolver = new FileSequenceResolver(directory.Path);

        Assert.Equal("inside", resolver.Resolve("inside.yaml").Id);
        // Both separators: a file authored on Windows must mean the same on a Linux host.
        Assert.Throws<InvalidOperationException>(() => resolver.Resolve(@"..\outside.yaml"));
        Assert.Throws<InvalidOperationException>(() => resolver.Resolve("../outside.yaml"));
        Assert.Throws<FileNotFoundException>(() => resolver.Resolve("missing.yaml"));
    }

    [Fact]
    public void Validator_ChecksFlowExpressionsWhereTheyAreEvaluated()
    {
        var looped = Item("looped", Step("read", ("echo", "${channel}")));
        looped.Loop = new LoopDefinition { Count = "${channels}", Variable = "channel" };
        looped.RunIf = "${undefinedFlag}";
        var polled = Step("poll");
        polled.VariableWrites.Add(new VariableWriteDefinition { Name = "soc", OutputKey = "calls" });
        polled.Retry = new RetryDefinition { MaxAttempts = 5, Until = "${soc} >= 95" };
        var badRetry = Step("bad");
        badRetry.Retry = new RetryDefinition { MaxAttempts = 0, Until = "${soc} >=" };
        var sequence = new TestSequence
        {
            Name = "Flow",
            Variables = { ["channels"] = 4 },
            Items = [looped, Item("polling", polled, badRetry)]
        };

        var issues = new TestSequenceValidator().Validate(sequence);

        Assert.Contains(issues, issue => issue.Path == "items[0].runIf" && issue.Message.Contains("undefinedFlag"));
        Assert.DoesNotContain(issues, issue => issue.Path.StartsWith("items[0].loop", StringComparison.Ordinal));
        Assert.DoesNotContain(issues, issue => issue.Path.StartsWith("items[0].main", StringComparison.Ordinal));
        Assert.DoesNotContain(issues, issue => issue.Path == "items[1].main[0].retry.until");
        Assert.Contains(issues, issue => issue.Path == "items[1].main[1].retry.maxAttempts");
        Assert.Contains(issues, issue => issue.Path == "items[1].main[1].retry.until" && issue.Message.Contains("not a valid expression"));
    }

    [Fact]
    public void Validator_RejectsStepsOnAGroup_AndChecksCallsAgainstTheResolver()
    {
        var group = new TestItemDefinition
        {
            Id = "group",
            Name = "Group",
            Items = [Item("child", Step("child"))],
            MainSteps = [Step("stray")]
        };
        var call = new TestItemDefinition
        {
            Id = "call",
            Name = "Call",
            Call = new SequenceCallDefinition { Path = "shared.yaml", Parameters = { ["tetsVoltage"] = 500, ["level"] = "${missing}" } }
        };
        var missing = new TestItemDefinition { Id = "missing", Name = "Missing", Call = new SequenceCallDefinition { Path = "nope.yaml" } };
        var sequence = new TestSequence { Name = "Tree", Items = [group, call, missing] };
        var resolver = new MapResolver { ["shared.yaml"] = new TestSequence { Variables = { ["testVoltage"] = 500, ["level"] = 1 } } };

        var issues = new TestSequenceValidator(sequences: resolver).Validate(sequence);

        Assert.Contains(issues, issue => issue.Path == "items[0]" && issue.Message.Contains("no steps of its own"));
        Assert.DoesNotContain(issues, issue => issue.Path.StartsWith("items[0].items[0]", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Path == "items[1].call.parameters.tetsVoltage" && issue.Severity == ValidationSeverity.Warning);
        Assert.Contains(issues, issue => issue.Path == "items[1].call.parameters.level" && issue.Message.Contains("not defined"));
        Assert.Contains(issues, issue => issue.Path == "items[2].call.path" && issue.Message.Contains("cannot be loaded"));
    }

    [Fact]
    public void Validator_RequiresUniqueIdsAcrossTheWholeTree()
    {
        var group = new TestItemDefinition { Id = "dup", Name = "Group", Items = [Item("dup", Step("s"))] };

        var issues = new TestSequenceValidator().Validate(new TestSequence { Name = "Tree", Items = [group] });

        Assert.Contains(issues, issue => issue.Path == "items[0].items[0].id");
    }

    private static TestItemDefinition Item(string id, params TestStepDefinition[] steps) => new()
    {
        Id = id,
        Name = id,
        MainSteps = [.. steps],
        VerdictSource = new VerdictSource { StepId = steps[^1].Id }
    };

    private static TestStepDefinition Step(string id, params (string Key, object? Value)[] parameters)
    {
        var step = new TestStepDefinition { Id = id, Name = id, PluginId = "probe.script" };
        foreach (var (key, value) in parameters)
        {
            step.Parameters[key] = value;
        }

        return step;
    }

    private static TestStepDefinition WithWrite(TestStepDefinition step, string variable, string output)
    {
        step.VariableWrites.Add(new VariableWriteDefinition { Name = variable, OutputKey = output });
        return step;
    }

    private static async Task<TestItemRunResult> RunItemAsync(ScriptPlugin plugin, TestStepDefinition step) =>
        await RunItemAsync(plugin, [step]);

    private static async Task<TestItemRunResult> RunItemAsync(
        ScriptPlugin plugin,
        TestStepDefinition[] steps,
        Dictionary<string, object?>? variables = null)
    {
        var item = Item("item", steps);
        var sequence = new TestSequence { Items = [item] };
        foreach (var (name, value) in variables ?? [])
        {
            sequence.Variables[name] = value;
        }

        return Assert.Single((await RunAsync(plugin, sequence)).ItemResults);
    }

    private static Task<TestSequenceRunResult> RunAsync(ScriptPlugin plugin, TestSequence sequence, ISequenceResolver? resolver = null)
    {
        var registry = new PluginRegistry();
        registry.Register(plugin);
        return new TestSequenceRunner(registry) { SequenceResolver = resolver }.RunAsync(sequence);
    }

    /// <summary>
    /// A plugin scripted by its parameters: <c>verdicts</c> and <c>values</c> are comma lists played
    /// one per call (the last repeats), <c>echo</c> comes back as an output, and <c>calls</c> counts
    /// the calls this step has made. "Error" throws, as a plugin that faults would.
    /// </summary>
    private sealed class ScriptPlugin(Version? version = null) : ITestStepPlugin
    {
        private readonly ConcurrentDictionary<string, int> _calls = new(StringComparer.OrdinalIgnoreCase);

        public TestStepPluginDescriptor Descriptor { get; } = new()
        {
            PluginId = "probe.script",
            DisplayName = "probe.script",
            Version = version ?? new Version(1, 0, 0)
        };

        public Type SettingsType => typeof(object);

        public int CallsTo(string stepId) => _calls.TryGetValue(stepId, out var calls) ? calls : 0;

        public object CreateDefaultSettings() => new Dictionary<string, object?>();

        public object LoadSettings(IReadOnlyDictionary<string, object?> parameters) =>
            new Dictionary<string, object?>(parameters, StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, object?> SaveSettings(object settings) => (Dictionary<string, object?>)settings;

        public Task<TestStepResult> ExecuteAsync(TestStepExecutionContext context, object settings, CancellationToken cancellationToken)
        {
            var values = (Dictionary<string, object?>)settings;
            var call = _calls.AddOrUpdate(context.Step.Id, 1, (_, previous) => previous + 1);
            var verdict = Enum.Parse<TestVerdict>(Pick(values, "verdicts", call) ?? "Pass");
            if (verdict == TestVerdict.Error)
            {
                throw new InvalidOperationException($"scripted error on call {call}");
            }

            var result = new TestStepResult { Verdict = verdict, Outputs = { ["calls"] = call } };
            if (Pick(values, "values", call) is { } value)
            {
                result.Outputs["value"] = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            }

            if (values.TryGetValue("echo", out var echo))
            {
                result.Outputs["echo"] = echo;
            }

            return Task.FromResult(result);
        }

        private static string? Pick(Dictionary<string, object?> values, string key, int call)
        {
            if (!values.TryGetValue(key, out var script) || script is not string text)
            {
                return null;
            }

            var entries = text.Split(',', StringSplitOptions.TrimEntries);
            return entries[Math.Min(call, entries.Length) - 1];
        }
    }

    private sealed class OverlapPlugin(bool threadSafe, int holdMs = 100) : ITestStepPlugin
    {
        private int _inside;
        private int _peak;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int PeakConcurrency => Volatile.Read(ref _peak);

        public TestStepPluginDescriptor Descriptor { get; } = new()
        {
            PluginId = "probe.overlap",
            DisplayName = "probe.overlap",
            Version = new Version(1, 0, 0)
        };

        public bool IsThreadSafe => threadSafe;

        public Type SettingsType => typeof(object);

        public object CreateDefaultSettings() => new();

        public object LoadSettings(IReadOnlyDictionary<string, object?> parameters) => new();

        public IReadOnlyDictionary<string, object?> SaveSettings(object settings) => new Dictionary<string, object?>();

        public async Task<TestStepResult> ExecuteAsync(TestStepExecutionContext context, object settings, CancellationToken cancellationToken)
        {
            var inside = Interlocked.Increment(ref _inside);
            int peak;
            while (inside > (peak = Volatile.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, inside, peak) != peak)
            {
            }

            Entered.TrySetResult();
            await Task.Delay(holdMs, CancellationToken.None);
            Interlocked.Decrement(ref _inside);
            return new TestStepResult { Verdict = TestVerdict.Pass };
        }
    }

    private sealed class MapResolver : Dictionary<string, TestSequence>, ISequenceResolver
    {
        public TestSequence Resolve(string path) =>
            TryGetValue(path, out var sequence) ? sequence : throw new FileNotFoundException($"No sequence '{path}'.");
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tf-flow-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
