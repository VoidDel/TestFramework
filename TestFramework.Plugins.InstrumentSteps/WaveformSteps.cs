using System.Globalization;
using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Plugins;
using TestFramework.Instruments;

namespace TestFramework.Plugins.InstrumentSteps;

public sealed class FunctionGeneratorSetStepPlugin() : InstrumentStepPlugin(
    "fgen.set", "设置信号发生器", "仪器/信号发生器", "配置一路输出的波形、频率、幅度（峰峰值）、偏置和占空比，并打开或关闭输出。")
{
    public override IReadOnlyList<StepParameterDescriptor> Parameters { get; } =
    [
        Resource(InstrumentClasses.FunctionGenerator, "信号发生器"),
        Choice<WaveformShape>("Shape", "波形", "正弦、方波、三角、上升/下降锯齿、脉冲或直流。", WaveformShape.Square),
        Number("Frequency", "频率", "单位 Hz。", minimum: 0, defaultValue: 1000.0),
        Number("Amplitude", "幅度", "峰峰值，单位 V。", minimum: 0, defaultValue: 5.0),
        Number("Offset", "偏置", "单位 V。", defaultValue: 0.0),
        Number("DutyCycle", "占空比", "方波与脉冲的占空比，单位 %。", minimum: 0, defaultValue: 50.0),
        Switch("Output", "输出", "配置完成后打开或关闭输出。")
    ];

    protected override async Task<TestStepResult> RunAsync(TestStepExecutionContext context, StepSettings settings, CancellationToken cancellationToken)
    {
        var generator = Instrument<IFunctionGenerator>(context, settings, InstrumentClasses.FunctionGenerator);
        var waveform = new WaveformSettings(
            settings.Choice("Shape", WaveformShape.Square),
            settings.Number("Frequency", 1000.0),
            settings.Number("Amplitude", 5.0),
            settings.Number("Offset", 0.0),
            settings.Number("DutyCycle", 50.0));
        await generator.ConfigureAsync(waveform, cancellationToken).ConfigureAwait(false);

        switch (settings.Choice("Output", SwitchAction.Unchanged))
        {
            case SwitchAction.On:
                await generator.SetOutputEnabledAsync(true, cancellationToken).ConfigureAwait(false);
                break;
            case SwitchAction.Off:
                await generator.SetOutputEnabledAsync(false, cancellationToken).ConfigureAwait(false);
                break;
        }

        return Pass();
    }
}

/// <summary>
/// Configures whichever of the three parts it is given - a channel when <c>Channel</c> is set, the
/// acquisition when <c>SampleRate</c> is, the trigger when <c>TriggerSource</c> is - so one step
/// can set a scope up and a later one can change only the trigger level.
/// </summary>
public sealed class OscilloscopeSetupStepPlugin() : InstrumentStepPlugin(
    "scope.setup", "设置示波器", "仪器/示波器", "配置通道（量程、偏置、耦合、探头衰减）、采集（采样率、记录长度）和边沿触发；给了哪部分就配哪部分。")
{
    public override IReadOnlyList<StepParameterDescriptor> Parameters { get; } =
    [
        Resource(InstrumentClasses.Oscilloscope, "示波器"),
        Text("Channel", "通道", "要配置的通道，如 CH1；不给则不配置通道。"),
        Number("Range", "量程", "通道满量程，单位 V；配置通道时必填。", minimum: 0),
        Number("Offset", "偏置", "通道偏置，单位 V。", defaultValue: 0.0),
        Choice<ScopeCoupling>("Coupling", "耦合", "直流、交流或接地。", ScopeCoupling.Dc),
        Number("ProbeAttenuation", "探头衰减", "10 表示 10:1 探头。", minimum: 0, defaultValue: 1.0),
        Number("SampleRate", "采样率", "单位 Hz；不给则不改采集设置。", minimum: 0),
        new StepParameterDescriptor
        {
            Name = "RecordLength",
            Kind = StepParameterKind.Integer,
            DisplayName = "记录长度",
            Description = "每次采集的点数。",
            Minimum = 2,
            DefaultValue = 1000
        },
        Text("TriggerSource", "触发源", "触发通道，如 CH1 或 EXT；不给则不改触发设置。"),
        Number("TriggerLevel", "触发电平", "单位 V。", defaultValue: 0.0),
        Choice<TriggerSlope>("TriggerSlope", "触发沿", "上升沿或下降沿。", TriggerSlope.Rising),
        Choice<TriggerMode>("TriggerMode", "触发方式", "Normal 等不到触发即超时；Auto 无触发也采集。", TriggerMode.Normal)
    ];

    protected override async Task<TestStepResult> RunAsync(TestStepExecutionContext context, StepSettings settings, CancellationToken cancellationToken)
    {
        var scope = Instrument<IOscilloscope>(context, settings, InstrumentClasses.Oscilloscope);
        if (settings.Text("Channel") is { } channel)
        {
            await scope.ConfigureChannelAsync(
                channel,
                new ScopeChannelSettings(
                    settings.RequiredNumber("Range"),
                    settings.Number("Offset", 0.0),
                    settings.Choice("Coupling", ScopeCoupling.Dc),
                    settings.Number("ProbeAttenuation", 1.0)),
                cancellationToken).ConfigureAwait(false);
        }

        if (settings.Number("SampleRate") is { } sampleRate)
        {
            var length = settings.Number("RecordLength", 1000);
            if (length != Math.Floor(length) || length < 2 || length > int.MaxValue)
            {
                throw new ArgumentException("Parameter 'RecordLength' must be a whole number of at least 2.");
            }

            await scope.ConfigureAcquisitionAsync(new ScopeAcquisitionSettings(sampleRate, (int)length), cancellationToken).ConfigureAwait(false);
        }

        if (settings.Text("TriggerSource") is { } source)
        {
            await scope.ConfigureTriggerAsync(
                new ScopeTriggerSettings(
                    source,
                    settings.Number("TriggerLevel", 0.0),
                    settings.Choice("TriggerSlope", TriggerSlope.Rising),
                    settings.Choice("TriggerMode", TriggerMode.Normal)),
                cancellationToken).ConfigureAwait(false);
        }

        return Pass();
    }
}

/// <summary>
/// Acquires, then outputs each measurement asked for under its own name in camel case -
/// <c>frequency</c>, <c>dutyCycle</c> - so a judgment's <c>outputKey</c> picks one and a check
/// another. With <c>IncludeSamples</c> the record goes out too, for a report or a calculation.
/// </summary>
public sealed class OscilloscopeMeasureStepPlugin() : InstrumentStepPlugin(
    "scope.measure", "示波器测量", "仪器/示波器", "采集一次并测量一个通道：每个测量项一个输出（frequency、dutyCycle、peakToPeak …），单位见 units。")
{
    public override IReadOnlyList<StepParameterDescriptor> Parameters { get; } =
    [
        Resource(InstrumentClasses.Oscilloscope, "示波器"),
        Text("Channel", "通道", "要测量的通道，如 CH1。", required: true),
        Text("Measurements", "测量项", $"逗号分隔：{string.Join(", ", Enum.GetNames<ScopeMeasurement>())}。", required: true),
        new StepParameterDescriptor
        {
            Name = "TimeoutMs",
            Kind = StepParameterKind.Integer,
            DisplayName = "触发超时",
            Description = "等待触发的毫秒数。",
            Minimum = 1,
            DefaultValue = 1000
        },
        new StepParameterDescriptor
        {
            Name = "Acquire",
            Kind = StepParameterKind.Boolean,
            DisplayName = "重新采集",
            Description = "false 则测量上一次采集的波形。",
            DefaultValue = true
        },
        new StepParameterDescriptor
        {
            Name = "IncludeSamples",
            Kind = StepParameterKind.Boolean,
            DisplayName = "输出波形",
            Description = "把采样点作为 samples 输出，sampleInterval 为采样间隔（s）。",
            DefaultValue = false
        }
    ];

    protected override async Task<TestStepResult> RunAsync(TestStepExecutionContext context, StepSettings settings, CancellationToken cancellationToken)
    {
        var scope = Instrument<IOscilloscope>(context, settings, InstrumentClasses.Oscilloscope);
        var channel = settings.Required("Channel");
        var measurements = settings.List("Measurements")
            .Select(name => Enum.TryParse<ScopeMeasurement>(name, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
                ? parsed
                : throw new ArgumentException($"'{name}' is not a scope measurement; use one of {string.Join(", ", Enum.GetNames<ScopeMeasurement>())}."))
            .ToArray();
        if (measurements.Length == 0)
        {
            throw new ArgumentException("Parameter 'Measurements' names no measurement.");
        }

        var result = Pass();
        if (settings.Flag("Acquire", true))
        {
            var timeout = TimeSpan.FromMilliseconds(settings.Number("TimeoutMs", 1000));
            var records = await scope.AcquireAsync([channel], timeout, cancellationToken).ConfigureAwait(false);
            var record = records.FirstOrDefault(candidate => string.Equals(candidate.Channel, channel, StringComparison.OrdinalIgnoreCase))
                         ?? throw new InvalidOperationException($"The acquisition returned no record for {channel}.");
            if (settings.Flag("IncludeSamples", false))
            {
                result.Outputs["samples"] = record.Samples.Cast<object?>().ToList();
                result.Outputs["sampleInterval"] = record.SampleIntervalSeconds;
            }
        }

        var units = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var measurement in measurements)
        {
            var key = CamelCase(measurement);
            result.Outputs[key] = await scope.MeasureAsync(channel, measurement, cancellationToken).ConfigureAwait(false);
            units[key] = ScopeMeasurements.UnitOf(measurement);
        }

        result.Outputs["units"] = units;
        return result;
    }

    private static string CamelCase(ScopeMeasurement measurement)
    {
        var name = measurement.ToString();
        return char.ToLower(name[0], CultureInfo.InvariantCulture) + name[1..];
    }
}
