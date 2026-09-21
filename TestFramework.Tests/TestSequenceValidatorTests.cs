using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Plugins;
using TestFramework.Abstractions.Resources;
using TestFramework.Core.Plugins;
using TestFramework.SequenceYaml.Validation;
using Xunit;

namespace TestFramework.Tests;

public sealed class TestSequenceValidatorTests
{
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Validate_ReportsNonFiniteNumericLimits(double limit)
    {
        var step = new TestStepDefinition
        {
            Id = "main",
            Name = "Main",
            PluginId = "demo.step",
            PluginVersion = "1.0.0"
        };
        var sequence = new TestSequence
        {
            Name = "Sequence",
            Items =
            [
                new TestItemDefinition
                {
                    Name = "Item",
                    MainSteps = [step],
                    VerdictSource = new VerdictSource
                    {
                        StepId = step.Id,
                        OutputKey = "value",
                        JudgeType = VerdictJudgeType.Numeric,
                        LowerLimit = limit,
                        UpperLimit = 10
                    }
                }
            ]
        };

        var issues = new TestSequenceValidator().Validate(sequence);

        Assert.Contains(issues, issue => issue.Path.EndsWith("lowerLimit", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ReportsMissingPluginVersion()
    {
        var registry = new PluginRegistry();
        registry.Register(new StubPlugin("demo.step", new Version(1, 0, 0)));

        var sequence = new TestSequence
        {
            Name = "Sequence",
            Items =
            [
                new TestItemDefinition
                {
                    Name = "Item",
                    MainSteps =
                    [
                        new TestStepDefinition
                        {
                            Id = "main",
                            Name = "Main",
                            PluginId = "demo.step",
                            PluginVersion = "2.0.0"
                        }
                    ],
                    VerdictSource = new VerdictSource { StepId = "main" }
                }
            ]
        };

        var issue = Assert.Single(new TestSequenceValidator(registry).Validate(sequence));

        Assert.Equal("items[0].main[0].pluginId", issue.Path);
        Assert.Contains("2.0.0", issue.Message);
    }

    [Fact]
    public void Validate_ReportsBrokenServiceTransportReference()
    {
        var sequence = new TestSequence
        {
            Name = "Sequence",
            Services =
            [
                new TestServiceDefinition
                {
                    Id = "ecu1-uds",
                    ServiceId = "uds.client",
                    Transport = "missing-transport"
                }
            ],
            Items =
            [
                new TestItemDefinition
                {
                    Name = "Item",
                    MainSteps =
                    [
                        new TestStepDefinition
                        {
                            Id = "main",
                            Name = "Main",
                            PluginId = "demo.step",
                            PluginVersion = "1.0.0"
                        }
                    ],
                    VerdictSource = new VerdictSource { StepId = "main" }
                }
            ]
        };

        var issues = new TestSequenceValidator().Validate(sequence);

        Assert.Contains(issues, issue => issue.Path == "services[0].transport");
    }

    [Fact]
    public void Validate_ReportsDuplicatedStepIdAcrossSections()
    {
        var sequence = new TestSequence
        {
            Name = "Sequence",
            Items =
            [
                new TestItemDefinition
                {
                    Name = "Item",
                    InitSteps =
                    [
                        new TestStepDefinition
                        {
                            Id = "shared",
                            Name = "Init",
                            PluginId = "demo.step",
                            PluginVersion = "1.0.0"
                        }
                    ],
                    MainSteps =
                    [
                        new TestStepDefinition
                        {
                            Id = "shared",
                            Name = "Main",
                            PluginId = "demo.step",
                            PluginVersion = "1.0.0"
                        }
                    ],
                    VerdictSource = new VerdictSource { StepId = "shared" }
                }
            ]
        };

        var issues = new TestSequenceValidator().Validate(sequence);

        Assert.Contains(issues, issue =>
            issue.Path == "items[0].main[0].id" &&
            issue.Message.Contains("duplicated", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_AllowsStandaloneTransportAndService()
    {
        var sequence = new TestSequence
        {
            Name = "Sequence",
            Transports =
            [
                new TransportDefinition
                {
                    Id = "psu1-tcp",
                    TransportId = "tcp.client"
                }
            ],
            Services =
            [
                new TestServiceDefinition
                {
                    Id = "crc16",
                    ServiceId = "crc16.calculator"
                }
            ],
            Items =
            [
                new TestItemDefinition
                {
                    Name = "Item",
                    MainSteps =
                    [
                        new TestStepDefinition
                        {
                            Id = "main",
                            Name = "Main",
                            PluginId = "demo.step",
                            PluginVersion = "1.0.0"
                        }
                    ],
                    VerdictSource = new VerdictSource { StepId = "main" }
                }
            ]
        };

        var issues = new TestSequenceValidator().Validate(sequence);

        Assert.DoesNotContain(issues, issue => issue.Path is "transports[0].channel" or "services[0].transport");
    }

    [Fact]
    public void Validate_ReportsBrokenTransportChannelReferenceWhenChannelIsSpecified()
    {
        var sequence = new TestSequence
        {
            Name = "Sequence",
            Transports =
            [
                new TransportDefinition
                {
                    Id = "isotp",
                    TransportId = "isotp",
                    Channel = "missing-can"
                }
            ],
            Items =
            [
                new TestItemDefinition
                {
                    Name = "Item",
                    MainSteps =
                    [
                        new TestStepDefinition
                        {
                            Id = "main",
                            Name = "Main",
                            PluginId = "demo.step",
                            PluginVersion = "1.0.0"
                        }
                    ],
                    VerdictSource = new VerdictSource { StepId = "main" }
                }
            ]
        };

        var issues = new TestSequenceValidator().Validate(sequence);

        Assert.Contains(issues, issue => issue.Path == "transports[0].channel");
    }

    [Fact]
    public void Validate_UnregisteredResourcePlugins_AreReportedBeforeTheRun()
    {
        var sequence = ValidSequence();
        sequence.Instruments.Add(new InstrumentDefinition { Id = "dmm", DriverId = "missing.driver", DriverVersion = "1.0.0" });
        sequence.Transports.Add(new TransportDefinition { Id = "bus", TransportId = "missing.transport", TransportVersion = "1.0.0" });
        sequence.Services.Add(new TestServiceDefinition { Id = "svc", ServiceId = "missing.service", ServiceVersion = "1.0.0" });

        var issues = new TestSequenceValidator(null, new EmptyResourceCatalog()).Validate(sequence);

        Assert.Contains(issues, issue => issue.Path == "instruments[0].driverId");
        Assert.Contains(issues, issue => issue.Path == "transports[0].transportId");
        Assert.Contains(issues, issue => issue.Path == "services[0].serviceId");
    }

    [Fact]
    public void Validate_WithoutAResourceCatalog_DoesNotReportResourcePlugins()
    {
        var sequence = ValidSequence();
        sequence.Instruments.Add(new InstrumentDefinition { Id = "dmm", DriverId = "missing.driver", DriverVersion = "1.0.0" });

        Assert.Empty(new TestSequenceValidator().Validate(sequence));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveTimeout_IsReported(int timeoutMs)
    {
        var sequence = ValidSequence();
        sequence.Items[0].MainSteps[0].TimeoutMs = timeoutMs;

        Assert.Contains(new TestSequenceValidator().Validate(sequence), issue => issue.Path == "items[0].main[0].timeoutMs");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-version")]
    public void Validate_InvalidPluginVersion_IsReported(string version)
    {
        var sequence = ValidSequence();
        sequence.Items[0].MainSteps[0].PluginVersion = version;

        Assert.Contains(new TestSequenceValidator().Validate(sequence), issue => issue.Path == "items[0].main[0].pluginVersion");
    }

    [Fact]
    public void Validate_VariableWriteWithoutOutputKeyOrValue_IsReported()
    {
        var sequence = ValidSequence();
        sequence.Items[0].MainSteps[0].VariableWrites.Add(new VariableWriteDefinition { Name = "result" });

        Assert.Contains(new TestSequenceValidator().Validate(sequence), issue => issue.Path == "items[0].main[0].variableWrites[0]");
    }

    [Fact]
    public void Validate_VerdictSourceOutsideMainSteps_IsReported()
    {
        var sequence = ValidSequence();
        sequence.Items[0].VerdictSource.StepId = "not-a-main-step";

        Assert.Contains(new TestSequenceValidator().Validate(sequence), issue => issue.Path == "items[0].verdictSource.stepId");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Validate_UnsupportedSchemaVersion_IsReported(int version)
    {
        var sequence = ValidSequence();
        sequence.SchemaVersion = version;

        Assert.Contains(new TestSequenceValidator().Validate(sequence), issue => issue.Path == "schemaVersion");
    }

    [Fact]
    public void Validate_UndefinedEnumValuesFromYaml_AreReported()
    {
        var sequence = ValidSequence();
        sequence.Items[0].VerdictSource.JudgeType = (VerdictJudgeType)99;
        sequence.Items[0].VerdictSource.StringMode = (StringJudgeMode)42;

        var issues = new TestSequenceValidator().Validate(sequence);

        Assert.Contains(issues, issue => issue.Path == "items[0].verdictSource.judgeType");
        Assert.Contains(issues, issue => issue.Path == "items[0].verdictSource.stringMode");
    }

    [Fact]
    public void Validate_SubstitutedPluginVersion_WarnsWithoutBlockingTheRun()
    {
        var registry = new PluginRegistry();
        registry.Register(new StubPlugin("demo.step", new Version(1, 2, 0)));
        var sequence = ValidSequence();
        sequence.Items[0].MainSteps[0].PluginVersion = "1.0.0";

        var issues = new TestSequenceValidator(registry).Validate(sequence);

        var warning = Assert.Single(issues);
        Assert.Equal(ValidationSeverity.Warning, warning.Severity);
        Assert.False(warning.IsError);
        Assert.Contains("1.2.0", warning.Message);
    }

    [Fact]
    public void Validate_IncompatiblePluginVersion_IsAnErrorNamingTheInstalledVersions()
    {
        var registry = new PluginRegistry();
        registry.Register(new StubPlugin("demo.step", new Version(2, 0, 0)));
        var sequence = ValidSequence();
        sequence.Items[0].MainSteps[0].PluginVersion = "1.0.0";

        var issue = Assert.Single(new TestSequenceValidator(registry).Validate(sequence));

        Assert.True(issue.IsError);
        Assert.Contains("2.0.0", issue.Message);
    }

    private static TestSequence ValidSequence()
    {
        return new TestSequence
        {
            Name = "Sequence",
            Items =
            [
                new TestItemDefinition
                {
                    Id = "item",
                    Name = "Item",
                    MainSteps =
                    [
                        new TestStepDefinition
                        {
                            Id = "main",
                            Name = "Main",
                            PluginId = "demo.step",
                            PluginVersion = "1.0.0"
                        }
                    ],
                    VerdictSource = new VerdictSource { StepId = "main" }
                }
            ]
        };
    }

    private sealed class EmptyResourceCatalog : IResourcePluginCatalog
    {
        public bool TryResolve(
            ResourcePluginKind kind,
            string pluginId,
            string? version,
            out PluginVersionMatch match,
            out Version resolvedVersion)
        {
            match = default;
            resolvedVersion = new Version(0, 0);
            return false;
        }

        public string DescribeMissing(ResourcePluginKind kind, string pluginId, string? version) =>
            $"{kind} '{pluginId}' is not registered.";
    }

    /// <summary>
    /// Builds a one-step sequence carrying <paramref name="parameters"/>, so the parameter checks
    /// below are not buried in sequence scaffolding.
    /// </summary>
    private static TestSequence SequenceWithParameters(
        Dictionary<string, object?> parameters,
        Dictionary<string, object?>? variables = null)
    {
        var step = new TestStepDefinition
        {
            Id = "main",
            Name = "Main",
            PluginId = "demo.step",
            PluginVersion = "1.0.0",
            Parameters = parameters
        };

        return new TestSequence
        {
            Name = "Sequence",
            Variables = variables ?? [],
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

    private static TestSequenceValidator ValidatorFor(params StepParameterDescriptor[] parameters)
    {
        var registry = new PluginRegistry();
        registry.Register(new StubPlugin("demo.step", new Version(1, 0, 0), parameters));
        return new TestSequenceValidator(registry);
    }

    [Fact]
    public void Validate_ParameterOfTheWrongType_IsReportedBeforeTheRun()
    {
        // The whole point of declaring parameters: this used to pass validation, save, and fail
        // part-way through a run - after earlier steps had already driven the DUT.
        var issues = ValidatorFor(new StepParameterDescriptor
            {
                Name = "DelayMs",
                Kind = StepParameterKind.Integer
            })
            .Validate(SequenceWithParameters(new Dictionary<string, object?> { ["DelayMs"] = "abc" }));

        var issue = Assert.Single(issues, candidate => candidate.Path.Contains("DelayMs", StringComparison.Ordinal));
        Assert.Equal(ValidationSeverity.Error, issue.Severity);
        Assert.Contains("whole number", issue.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(20001)]
    public void Validate_ParameterOutsideItsRange_IsReported(int value)
    {
        var issues = ValidatorFor(new StepParameterDescriptor
            {
                Name = "DelayMs",
                Kind = StepParameterKind.Integer,
                Minimum = 0,
                Maximum = 20000
            })
            .Validate(SequenceWithParameters(new Dictionary<string, object?> { ["DelayMs"] = value }));

        Assert.Contains(issues, issue =>
            issue.Severity == ValidationSeverity.Error &&
            issue.Path.Contains("DelayMs", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RequiredParameterMissing_IsReported()
    {
        var issues = ValidatorFor(new StepParameterDescriptor
            {
                Name = "Port",
                Kind = StepParameterKind.String,
                IsRequired = true
            })
            .Validate(SequenceWithParameters([]));

        Assert.Contains(issues, issue =>
            issue.Severity == ValidationSeverity.Error &&
            issue.Message.Contains("required", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_RequiredParameterWithADefault_IsSatisfiedByTheDefault()
    {
        var issues = ValidatorFor(new StepParameterDescriptor
            {
                Name = "DelayMs",
                Kind = StepParameterKind.Integer,
                IsRequired = true,
                DefaultValue = 500
            })
            .Validate(SequenceWithParameters([]));

        Assert.DoesNotContain(issues, issue => issue.Path.Contains("DelayMs", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ValueOutsideTheDeclaredChoices_IsReported()
    {
        var issues = ValidatorFor(new StepParameterDescriptor
            {
                Name = "Mode",
                Kind = StepParameterKind.Enum,
                Choices = [new StepParameterChoice("cc"), new StepParameterChoice("cv")]
            })
            .Validate(SequenceWithParameters(new Dictionary<string, object?> { ["Mode"] = "cx" }));

        var issue = Assert.Single(issues, candidate => candidate.Path.Contains("Mode", StringComparison.Ordinal));
        Assert.Contains("'cc', 'cv'", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_VariableReference_SuspendsTypeCheckingButNotTheVariableItself()
    {
        var descriptor = new StepParameterDescriptor { Name = "DelayMs", Kind = StepParameterKind.Integer };

        // A reference to a variable that exists is fine: its value is only known at run time, so the
        // type cannot be checked here and reporting it would make every real sequence invalid.
        var defined = ValidatorFor(descriptor).Validate(SequenceWithParameters(
            new Dictionary<string, object?> { ["DelayMs"] = "${soakMs}" },
            new Dictionary<string, object?> { ["soakMs"] = 1000 }));
        Assert.DoesNotContain(defined, issue => issue.Path.Contains("DelayMs", StringComparison.Ordinal));

        // A reference to one that does not exist is a run-time crash waiting to happen.
        var undefined = ValidatorFor(descriptor).Validate(SequenceWithParameters(
            new Dictionary<string, object?> { ["DelayMs"] = "${soakMs}" }));
        Assert.Contains(undefined, issue =>
            issue.Severity == ValidationSeverity.Error &&
            issue.Message.Contains("soakMs", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ParameterThatForbidsVariables_ReportsAReference()
    {
        var issues = ValidatorFor(new StepParameterDescriptor
            {
                Name = "Channel",
                Kind = StepParameterKind.String,
                AllowVariableReference = false
            })
            .Validate(SequenceWithParameters(
                new Dictionary<string, object?> { ["Channel"] = "${channel}" },
                new Dictionary<string, object?> { ["channel"] = "can0" }));

        Assert.Contains(issues, issue => issue.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void Validate_UndeclaredParameter_IsAWarningNotAnError()
    {
        // Usually a typo, but it may also be a key a newer build of the plugin reads, so it must not
        // refuse the sequence.
        var issues = ValidatorFor(new StepParameterDescriptor { Name = "DelayMs", Kind = StepParameterKind.Integer })
            .Validate(SequenceWithParameters(new Dictionary<string, object?> { ["DelyMs"] = 500 }));

        var issue = Assert.Single(issues, candidate => candidate.Path.Contains("DelyMs", StringComparison.Ordinal));
        Assert.Equal(ValidationSeverity.Warning, issue.Severity);
    }

    [Fact]
    public void Validate_PluginThatDeclaresNoParameters_IsLeftAlone()
    {
        // Every plugin built before this member existed lands here. Reporting anything would make
        // working sequences invalid on upgrade.
        var issues = ValidatorFor()
            .Validate(SequenceWithParameters(new Dictionary<string, object?> { ["anything"] = "at all" }));

        Assert.DoesNotContain(issues, issue => issue.Path.Contains("parameters", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RequirementTheStationDoesNotBind_IsReportedBeforeTheRun()
    {
        var sequence = new TestSequence
        {
            Name = "Sequence",
            Requires = [new ResourceRequirement { Alias = "psu", Description = "程控电源" }],
            Items =
            [
                new TestItemDefinition
                {
                    Name = "Item",
                    MainSteps = [new TestStepDefinition { Id = "main", Name = "Main", PluginId = "demo.step" }],
                    VerdictSource = new VerdictSource { StepId = "main" }
                }
            ]
        };

        var station = new StationConfiguration { StationId = "line-1", Name = "工位 1" };
        var issues = new TestSequenceValidator(station: station).Validate(sequence);

        // Without this the operator learns the bench has no supply once a DUT is already connected.
        var issue = Assert.Single(issues, candidate => candidate.Path.StartsWith("requires", StringComparison.Ordinal));
        Assert.Contains("工位 1", issue.Message, StringComparison.Ordinal);
        Assert.Contains("psu", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RequirementBoundByTheStation_IsAccepted()
    {
        var sequence = new TestSequence
        {
            Name = "Sequence",
            Requires = [new ResourceRequirement { Alias = "psu" }],
            Items =
            [
                new TestItemDefinition
                {
                    Name = "Item",
                    MainSteps = [new TestStepDefinition { Id = "main", Name = "Main", PluginId = "demo.step" }],
                    VerdictSource = new VerdictSource { StepId = "main" }
                }
            ]
        };

        var station = new StationConfiguration
        {
            StationId = "line-1",
            Resources = [new StationResourceBinding { Alias = "psu", DriverId = "demo.psu", Resource = "COM7" }]
        };

        var issues = new TestSequenceValidator(station: station).Validate(sequence);

        Assert.DoesNotContain(issues, issue => issue.Path.StartsWith("requires", StringComparison.Ordinal));
    }

    private sealed class StubPlugin : ITestStepPlugin
    {
        public StubPlugin(string pluginId, Version version, params StepParameterDescriptor[] parameters)
        {
            Descriptor = new TestStepPluginDescriptor
            {
                PluginId = pluginId,
                DisplayName = pluginId,
                Version = version
            };
            Parameters = parameters;
        }

        public TestStepPluginDescriptor Descriptor { get; }

        public IReadOnlyList<StepParameterDescriptor> Parameters { get; }

        public Type SettingsType => typeof(object);

        public object CreateDefaultSettings() => new();

        public object LoadSettings(IReadOnlyDictionary<string, object?> parameters) => new();

        public IReadOnlyDictionary<string, object?> SaveSettings(object settings) => new Dictionary<string, object?>();

        public Task<TestStepResult> ExecuteAsync(
            TestStepExecutionContext context,
            object settings,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new TestStepResult { Verdict = TestVerdict.Pass });
        }
    }
}
