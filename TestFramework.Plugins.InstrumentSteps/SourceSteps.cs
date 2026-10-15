using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Plugins;
using TestFramework.Instruments;

namespace TestFramework.Plugins.InstrumentSteps;

/// <summary>
/// Sets what is given and leaves the rest: the protection first, so a voltage above it never
/// reaches the output, then the limit, the voltage, and the output switch last.
/// </summary>
public sealed class PowerSupplySetStepPlugin() : InstrumentStepPlugin(
    "psu.set", "设置电源", "仪器/电源", "设置一路直流电源的电压、限流、过压保护和输出开关；没给的参数保持不变。")
{
    public override IReadOnlyList<StepParameterDescriptor> Parameters { get; } =
    [
        Resource(InstrumentClasses.PowerSupply, "电源"),
        Number("Voltage", "电压", "输出电压，单位 V。", minimum: 0),
        Number("CurrentLimit", "限流", "限流值，单位 A。", minimum: 0),
        Number("OverVoltageProtection", "过压保护", "过压保护阈值，单位 V。", minimum: 0),
        Switch("Output", "输出", "设置完成后打开或关闭输出。")
    ];

    protected override async Task<TestStepResult> RunAsync(TestStepExecutionContext context, StepSettings settings, CancellationToken cancellationToken)
    {
        var supply = Instrument<IPowerSupply>(context, settings, InstrumentClasses.PowerSupply);
        if (settings.Number("OverVoltageProtection") is { } protection)
        {
            await supply.SetOverVoltageProtectionAsync(protection, cancellationToken).ConfigureAwait(false);
        }

        if (settings.Number("CurrentLimit") is { } limit)
        {
            await supply.SetCurrentLimitAsync(limit, cancellationToken).ConfigureAwait(false);
        }

        if (settings.Number("Voltage") is { } voltage)
        {
            await supply.SetVoltageAsync(voltage, cancellationToken).ConfigureAwait(false);
        }

        var output = settings.Choice("Output", SwitchAction.Unchanged);
        switch (output)
        {
            case SwitchAction.On:
                await supply.SetOutputEnabledAsync(true, cancellationToken).ConfigureAwait(false);
                break;
            case SwitchAction.Off:
                await supply.SetOutputEnabledAsync(false, cancellationToken).ConfigureAwait(false);
                break;
        }

        // Switching on into a tripped protection leaves the output off; that is not "set". Switching
        // off after a trip is the safe thing a cleanup does, and it succeeded.
        var status = await supply.QueryStatusAsync(cancellationToken).ConfigureAwait(false);
        var outputs = new (string, object?)[] { ("outputEnabled", status.OutputEnabled), ("mode", status.Mode.ToString()), ("protectionTripped", status.ProtectionTripped) };
        return output == SwitchAction.On && !status.OutputEnabled
            ? Fail(status.ProtectionTripped ? "The supply's protection has tripped and its output stayed off." : "The supply's output did not switch on.", outputs)
            : Pass(outputs);
    }
}

public sealed class PowerSupplyMeasureStepPlugin() : InstrumentStepPlugin(
    "psu.measure", "测量电源输出", "仪器/电源", "回读一路直流电源的输出电压（V）、电流（A）和工作状态。")
{
    public override IReadOnlyList<StepParameterDescriptor> Parameters { get; } =
    [
        Resource(InstrumentClasses.PowerSupply, "电源")
    ];

    protected override async Task<TestStepResult> RunAsync(TestStepExecutionContext context, StepSettings settings, CancellationToken cancellationToken)
    {
        var supply = Instrument<IPowerSupply>(context, settings, InstrumentClasses.PowerSupply);
        var voltage = await supply.MeasureVoltageAsync(cancellationToken).ConfigureAwait(false);
        var current = await supply.MeasureCurrentAsync(cancellationToken).ConfigureAwait(false);
        var status = await supply.QueryStatusAsync(cancellationToken).ConfigureAwait(false);
        return Pass(
            ("voltage", voltage),
            ("current", current),
            ("power", voltage * current),
            ("mode", status.Mode.ToString()),
            ("outputEnabled", status.OutputEnabled),
            ("protectionTripped", status.ProtectionTripped));
    }
}

public sealed class ElectronicLoadSetStepPlugin() : InstrumentStepPlugin(
    "load.set", "设置电子负载", "仪器/电子负载", "设置电子负载的工作模式和设定值，并打开或关闭输入。")
{
    public override IReadOnlyList<StepParameterDescriptor> Parameters { get; } =
    [
        Resource(InstrumentClasses.ElectronicLoad, "电子负载"),
        Choice<LoadMode>("Mode", "模式", "恒流、恒压、恒阻或恒功率。", LoadMode.ConstantCurrent),
        Number("Level", "设定值", "按模式：电流 A、电压 V、电阻 Ohm 或功率 W。不给则只切换输入。", minimum: 0),
        Switch("Input", "输入", "设置完成后打开或关闭输入。")
    ];

    protected override async Task<TestStepResult> RunAsync(TestStepExecutionContext context, StepSettings settings, CancellationToken cancellationToken)
    {
        var load = Instrument<IElectronicLoad>(context, settings, InstrumentClasses.ElectronicLoad);
        if (settings.Number("Level") is { } level)
        {
            await load.SetModeAsync(settings.Choice("Mode", LoadMode.ConstantCurrent), level, cancellationToken).ConfigureAwait(false);
        }

        switch (settings.Choice("Input", SwitchAction.Unchanged))
        {
            case SwitchAction.On:
                await load.SetInputEnabledAsync(true, cancellationToken).ConfigureAwait(false);
                break;
            case SwitchAction.Off:
                await load.SetInputEnabledAsync(false, cancellationToken).ConfigureAwait(false);
                break;
        }

        return Pass();
    }
}

public sealed class ElectronicLoadMeasureStepPlugin() : InstrumentStepPlugin(
    "load.measure", "测量电子负载", "仪器/电子负载", "回读电子负载输入端的电压（V）、电流（A）和功率（W）。")
{
    public override IReadOnlyList<StepParameterDescriptor> Parameters { get; } =
    [
        Resource(InstrumentClasses.ElectronicLoad, "电子负载")
    ];

    protected override async Task<TestStepResult> RunAsync(TestStepExecutionContext context, StepSettings settings, CancellationToken cancellationToken)
    {
        var load = Instrument<IElectronicLoad>(context, settings, InstrumentClasses.ElectronicLoad);
        var voltage = await load.MeasureVoltageAsync(cancellationToken).ConfigureAwait(false);
        var current = await load.MeasureCurrentAsync(cancellationToken).ConfigureAwait(false);
        return Pass(("voltage", voltage), ("current", current), ("power", voltage * current));
    }
}
