using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Plugins;
using TestFramework.Instruments;

namespace TestFramework.Plugins.InstrumentSteps;

/// <summary>
/// One reading as <c>value</c>, in the function's unit, which <c>unit</c> names: a judgment with
/// <c>sourceUnit</c> set to it converts to limits written in mV or kOhm.
/// </summary>
public sealed class MultimeterMeasureStepPlugin() : InstrumentStepPlugin(
    "dmm.measure", "万用表测量", "仪器/万用表", "按测量功能读一次万用表；输出 value，单位见 unit（V、A、Ohm、Hz、°C）。")
{
    public override IReadOnlyList<StepParameterDescriptor> Parameters { get; } =
    [
        Resource(InstrumentClasses.DigitalMultimeter, "万用表"),
        Choice<MeasurementFunction>("Function", "测量功能", "直流/交流电压、电流，两线/四线电阻，频率或温度。", MeasurementFunction.DcVoltage),
        Number("Range", "量程", "预期的最大读数，单位同测量功能；不给则自动量程。", minimum: 0)
    ];

    protected override async Task<TestStepResult> RunAsync(TestStepExecutionContext context, StepSettings settings, CancellationToken cancellationToken)
    {
        var meter = Instrument<IDigitalMultimeter>(context, settings, InstrumentClasses.DigitalMultimeter);
        var function = settings.Choice("Function", MeasurementFunction.DcVoltage);
        var value = await meter.MeasureAsync(function, settings.Number("Range"), cancellationToken).ConfigureAwait(false);
        return Pass(("value", value), ("unit", MeasurementFunctions.UnitOf(function)));
    }
}

/// <summary>
/// The scan as one dictionary output keyed by channel, so one judgment checks every channel against
/// the same limits and records each - 32 temperatures are one step and one judgment, not 32.
/// </summary>
public sealed class DataAcquisitionScanStepPlugin() : InstrumentStepPlugin(
    "daq.scan", "数据采集扫描", "仪器/数据采集", "一次扫描多个通道；输出 values（按通道的字典）、min、max、spread，单位见 unit。")
{
    public override IReadOnlyList<StepParameterDescriptor> Parameters { get; } =
    [
        Resource(InstrumentClasses.DataAcquisition, "数据采集仪"),
        Text("Channels", "通道", "通道列表：101,102,105 或范围 101-132，按仪器自己的命名。", required: true),
        Choice<MeasurementFunction>("Function", "测量功能", "所有通道用同一测量功能。", MeasurementFunction.DcVoltage)
    ];

    protected override async Task<TestStepResult> RunAsync(TestStepExecutionContext context, StepSettings settings, CancellationToken cancellationToken)
    {
        var daq = Instrument<IDataAcquisition>(context, settings, InstrumentClasses.DataAcquisition);
        var channels = settings.List("Channels");
        if (channels.Count == 0)
        {
            throw new ArgumentException("Parameter 'Channels' names no channel.");
        }

        var function = settings.Choice("Function", MeasurementFunction.DcVoltage);
        var readings = await daq.ScanAsync(channels, function, cancellationToken).ConfigureAwait(false);
        var values = channels.ToDictionary(
            channel => channel,
            channel => readings.TryGetValue(channel, out var value)
                ? (object?)value
                : throw new InvalidOperationException($"The scan returned no reading for channel '{channel}'."),
            StringComparer.Ordinal);
        var numbers = values.Values.Cast<double>().ToArray();
        return Pass(
            ("values", values),
            ("min", numbers.Min()),
            ("max", numbers.Max()),
            ("spread", numbers.Max() - numbers.Min()),
            ("unit", MeasurementFunctions.UnitOf(function)));
    }
}
