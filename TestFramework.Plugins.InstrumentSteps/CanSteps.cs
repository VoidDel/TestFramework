using System.Diagnostics;
using System.Globalization;
using TestFramework.Abstractions.Execution;
using TestFramework.Abstractions.Plugins;
using TestFramework.Instruments;

namespace TestFramework.Plugins.InstrumentSteps;

public sealed class CanSendStepPlugin() : InstrumentStepPlugin(
    "can.send", "发送 CAN 帧", "通信/CAN", "在一个 CAN 通道上发送一帧，不等回复。")
{
    public override IReadOnlyList<StepParameterDescriptor> Parameters { get; } =
    [
        Resource(InstrumentClasses.CanChannel, "CAN 通道"),
        .. CanFrames.FrameParameters
    ];

    protected override async Task<TestStepResult> RunAsync(TestStepExecutionContext context, StepSettings settings, CancellationToken cancellationToken)
    {
        var channel = Transport<ICanChannel>(context, settings, InstrumentClasses.CanChannel);
        var frame = CanFrames.FromSettings(settings);
        await channel.SendAsync(frame, cancellationToken).ConfigureAwait(false);
        return Pass(("sent", frame.ToString()));
    }
}

/// <summary>
/// Sends a frame and waits for the reply, with the reader opened before the send so a reply faster
/// than the step is not lost. No reply in time, or a reply that does not match <c>Expect</c>, is a
/// <c>Fail</c>: the DUT did not answer as it should, which is a finding about the DUT.
/// </summary>
public sealed class CanRequestStepPlugin() : InstrumentStepPlugin(
    "can.request", "CAN 请求/响应", "通信/CAN", "发送一帧并等待响应帧；超时或响应与 Expect 不符判 Fail。输出 data（十六进制）、bytes（字节列表）、responseId、latencyMs。")
{
    public override IReadOnlyList<StepParameterDescriptor> Parameters { get; } =
    [
        Resource(InstrumentClasses.CanChannel, "CAN 通道"),
        .. CanFrames.FrameParameters,
        Text("ResponseId", "响应 id", "期望的响应帧 id，如 0x7E8。", required: true),
        Text("ResponseMask", "响应掩码", "按位比较的掩码；不给则 id 完全相同才算。"),
        .. CanFrames.WaitParameters
    ];

    protected override async Task<TestStepResult> RunAsync(TestStepExecutionContext context, StepSettings settings, CancellationToken cancellationToken)
    {
        var channel = Transport<ICanChannel>(context, settings, InstrumentClasses.CanChannel);
        var request = CanFrames.FromSettings(settings);
        var filter = CanFrames.Filter(settings, "ResponseId", "ResponseMask");
        using var reader = channel.OpenReader(filter);
        var clock = Stopwatch.StartNew();
        await channel.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await CanFrames.AwaitAsync(reader, settings, clock, $"No reply to {request} matching 0x{filter.Id:X}", cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Waits for a frame the DUT sends on its own - a status broadcast, a heartbeat.</summary>
public sealed class CanExpectStepPlugin() : InstrumentStepPlugin(
    "can.expect", "等待 CAN 帧", "通信/CAN", "等待一帧匹配 id（和掩码）的帧；超时或数据与 Expect 不符判 Fail。输出同 CAN 请求/响应。")
{
    public override IReadOnlyList<StepParameterDescriptor> Parameters { get; } =
    [
        Resource(InstrumentClasses.CanChannel, "CAN 通道"),
        Text("Id", "帧 id", "等待的帧 id，如 0x18FF50E5。", required: true),
        Text("Mask", "掩码", "按位比较的掩码；不给则 id 完全相同才算。"),
        .. CanFrames.WaitParameters
    ];

    protected override async Task<TestStepResult> RunAsync(TestStepExecutionContext context, StepSettings settings, CancellationToken cancellationToken)
    {
        var channel = Transport<ICanChannel>(context, settings, InstrumentClasses.CanChannel);
        var filter = CanFrames.Filter(settings, "Id", "Mask");
        using var reader = channel.OpenReader(filter);
        return await CanFrames.AwaitAsync(reader, settings, Stopwatch.StartNew(), $"No frame matching 0x{filter.Id:X}", cancellationToken).ConfigureAwait(false);
    }
}

internal static class CanFrames
{
    public static readonly StepParameterDescriptor[] FrameParameters =
    [
        new() { Name = "Id", Kind = StepParameterKind.String, DisplayName = "帧 id", Description = "如 0x7E0；29 位 id 同时把 Extended 设为 true。", IsRequired = true },
        new() { Name = "Data", Kind = StepParameterKind.String, DisplayName = "数据", Description = "十六进制字节，如 \"02 10 03\"。", DefaultValue = "" },
        new() { Name = "Extended", Kind = StepParameterKind.Boolean, DisplayName = "扩展帧", Description = "29 位扩展 id。", DefaultValue = false },
        new() { Name = "Fd", Kind = StepParameterKind.Boolean, DisplayName = "CAN FD", Description = "按 CAN FD 帧发送。", DefaultValue = false },
        new() { Name = "BitRateSwitch", Kind = StepParameterKind.Boolean, DisplayName = "速率切换", Description = "CAN FD 数据段加速（BRS）。", DefaultValue = false }
    ];

    public static readonly StepParameterDescriptor[] WaitParameters =
    [
        new() { Name = "TimeoutMs", Kind = StepParameterKind.Integer, DisplayName = "超时", Description = "等待的毫秒数。", Minimum = 1, DefaultValue = 100 },
        new()
        {
            Name = "Expect",
            Kind = StepParameterKind.String,
            DisplayName = "期望数据",
            Description = "从首字节起按前缀比较的十六进制字节，?? 表示任意，如 \"06 50 03 ?? ??\"；不给则任何数据都算。"
        }
    ];

    public static CanFrame FromSettings(StepSettings settings)
    {
        var data = settings.Text("Data") is { Length: > 0 } text ? CanFrame.ParseHex(text) : [];
        return new CanFrame(
            CanFrame.ParseId(settings.Required("Id")),
            data,
            settings.Flag("Extended", false),
            settings.Flag("Fd", false),
            settings.Flag("BitRateSwitch", false));
    }

    public static CanFilter Filter(StepSettings settings, string idParameter, string maskParameter) =>
        new(
            CanFrame.ParseId(settings.Required(idParameter)),
            settings.Text(maskParameter) is { } mask ? CanFrame.ParseId(mask) : CanFrame.MaxExtendedId,
            settings.Has("Extended") ? settings.Flag("Extended", false) : null);

    public static async Task<TestStepResult> AwaitAsync(
        ICanReader reader,
        StepSettings settings,
        Stopwatch clock,
        string timeoutMessage,
        CancellationToken cancellationToken)
    {
        var timeoutMs = settings.Number("TimeoutMs", 100);
        var frame = await reader.ReadAsync(TimeSpan.FromMilliseconds(timeoutMs), cancellationToken).ConfigureAwait(false);
        if (frame is null)
        {
            return InstrumentStepPlugin.Fail($"{timeoutMessage} within {timeoutMs.ToString(CultureInfo.InvariantCulture)} ms.");
        }

        var outputs = new (string, object?)[]
        {
            ("responseId", (long)frame.Id),
            ("data", frame.DataHex),
            ("bytes", frame.Data.ToArray().Select(value => (object?)(int)value).ToList()),
            ("length", frame.Data.Length),
            ("latencyMs", clock.Elapsed.TotalMilliseconds)
        };

        return settings.Text("Expect") is { Length: > 0 } expected && !Matches(frame.Data.Span, expected)
            ? InstrumentStepPlugin.Fail($"Received {frame}, which does not match the expected {expected}.", outputs)
            : InstrumentStepPlugin.Pass(outputs);
    }

    /// <summary>A prefix match of hex bytes, "??" matching any byte.</summary>
    private static bool Matches(ReadOnlySpan<byte> data, string pattern)
    {
        var tokens = pattern.Split([' ', ',', '-'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length > data.Length)
        {
            return false;
        }

        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? tokens[index][2..] : tokens[index];
            if (token is "??" or "xx" or "XX")
            {
                continue;
            }

            if (!byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var expected))
            {
                throw new ArgumentException($"Parameter 'Expect': '{tokens[index]}' is not a hex byte or ??.");
            }

            if (data[index] != expected)
            {
                return false;
            }
        }

        return true;
    }
}
