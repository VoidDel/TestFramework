using System.Globalization;
using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Resources;
using TestFramework.Instruments;

namespace TestFramework.Plugins.Simulation;

public sealed class SimulatedCanChannelDriver : ITransportPlugin
{
    public ResourcePluginDescriptor Descriptor { get; } = new()
    {
        PluginId = "sim.can",
        DisplayName = "模拟 CAN 通道",
        Version = new Version(1, 0, 0),
        Category = "模拟仪器",
        Description = "模拟一个 ECU：Responses 块按请求 id 回一帧（\"0x7E0\": \"0x7E8 02 50 03\"），Broadcast 块按 BroadcastIntervalMs（默认 100）周期发帧；Loopback 为 true 时发出的帧也能收到。"
    };

    public Type TransportType => typeof(SimulatedCanChannel);

    public Task<object> CreateAsync(TransportDefinition definition, IResourceScope resources, CancellationToken cancellationToken) =>
        Task.FromResult<object>(new SimulatedCanChannel(new SimulationSettings(definition.Id, definition.Settings)));
}

/// <summary>
/// A CAN channel with an ECU on the other end that answers configured requests at once - faster
/// than any step could start listening after sending, which is exactly the case a reader opened
/// before the request exists for - and broadcasts configured frames periodically, as a BMS sends
/// its status.
/// </summary>
public sealed class SimulatedCanChannel : ICanChannel, IDisposable
{
    private readonly CanFrameDispatcher _dispatcher = new();
    private readonly Dictionary<uint, CanFrame> _responses = [];
    private readonly IReadOnlyList<CanFrame> _broadcast;
    private readonly bool _loopback;
    private readonly Timer? _timer;
    private readonly List<CanFrame> _sent = [];

    internal SimulatedCanChannel(SimulationSettings settings)
    {
        _loopback = settings.Flag("Loopback", false);
        foreach (var (request, response) in settings.Map("Responses"))
        {
            _responses[Id(settings, request)] = Frame(settings, $"Responses.{request}", Convert.ToString(response, CultureInfo.InvariantCulture) ?? string.Empty);
        }

        _broadcast = settings.Map("Broadcast")
            .Select(pair => Frame(Id(settings, pair.Key), Bytes(settings, $"Broadcast.{pair.Key}", Convert.ToString(pair.Value, CultureInfo.InvariantCulture) ?? string.Empty)))
            .ToArray();
        if (_broadcast.Count > 0)
        {
            var interval = TimeSpan.FromMilliseconds(settings.Number("BroadcastIntervalMs", 100));
            _timer = new Timer(_ => Broadcast(), null, TimeSpan.Zero, interval);
        }
    }

    /// <summary>Frames this channel has sent, oldest first - what a test of a step can assert on.</summary>
    public IReadOnlyList<CanFrame> Sent
    {
        get
        {
            lock (_sent)
            {
                return [.. _sent];
            }
        }
    }

    public Task SendAsync(CanFrame frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        cancellationToken.ThrowIfCancellationRequested();
        var stamped = frame.WithTimestamp(DateTimeOffset.Now);
        lock (_sent)
        {
            _sent.Add(stamped);
        }

        if (_loopback)
        {
            _dispatcher.Publish(stamped);
        }

        if (_responses.TryGetValue(frame.Id, out var response))
        {
            _dispatcher.Publish(response.WithTimestamp(DateTimeOffset.Now));
        }

        return Task.CompletedTask;
    }

    public ICanReader OpenReader(CanFilter? filter = null) => _dispatcher.OpenReader(filter);

    public void Dispose() => _timer?.Dispose();

    private void Broadcast()
    {
        foreach (var frame in _broadcast)
        {
            _dispatcher.Publish(frame.WithTimestamp(DateTimeOffset.Now));
        }
    }

    /// <summary>"0x7E8 02 50 03": the response id, then its data.</summary>
    private static CanFrame Frame(SimulationSettings settings, string key, string text)
    {
        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            throw new ArgumentException($"Simulated CAN channel '{settings.Alias}': {key} needs a response id and data, such as \"0x7E8 02 50 03\".");
        }

        return Frame(Id(settings, parts[0]), parts.Length > 1 ? Bytes(settings, key, parts[1]) : []);
    }

    /// <summary>An id past 11 bits is written in the station file as it is, and is an extended frame.</summary>
    private static CanFrame Frame(uint id, byte[] data) => new(id, data, isExtended: id > CanFrame.MaxStandardId);

    private static uint Id(SimulationSettings settings, string text)
    {
        try
        {
            return CanFrame.ParseId(text);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException($"Simulated CAN channel '{settings.Alias}': {ex.Message}", ex);
        }
    }

    private static byte[] Bytes(SimulationSettings settings, string key, string text)
    {
        try
        {
            return CanFrame.ParseHex(text);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException($"Simulated CAN channel '{settings.Alias}': {key}: {ex.Message}", ex);
        }
    }
}
