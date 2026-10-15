using System.Globalization;
using TestFramework.Abstractions.Resources;

namespace TestFramework.Instruments;

/// <summary>
/// One CAN or CAN FD channel of an adapter - a transport, not an instrument: protocols such as UDS
/// or a BMS's own signals run over it as services, and the channel only moves frames.
///
/// Receiving goes through a reader opened before the request is sent. A request/response exchange
/// written as "send, then wait for the reply" loses a reply that arrives before the wait starts -
/// which on a fast ECU is most of them - so the reader buffers from the moment it opens:
/// <code>
/// using var reader = can.OpenReader(new CanFilter(0x7E8));
/// await can.SendAsync(request, ct);
/// var reply = await reader.ReadAsync(TimeSpan.FromMilliseconds(100), ct);
/// </code>
/// Bit rates and the adapter's own options are the transport's settings in the station file, not
/// part of this interface.
/// </summary>
[ResourceClass(InstrumentClasses.CanChannel)]
public interface ICanChannel
{
    Task SendAsync(CanFrame frame, CancellationToken cancellationToken = default);

    /// <summary>
    /// A reader that keeps every frame matching <paramref name="filter"/> - all frames when null -
    /// received from now until it is disposed. Readers are independent: each sees every matching
    /// frame, so two steps waiting on different ids do not take frames from each other.
    /// </summary>
    ICanReader OpenReader(CanFilter? filter = null);
}

public interface ICanReader : IDisposable
{
    /// <summary>
    /// The next frame the reader kept, waiting up to <paramref name="timeout"/> for one; null when
    /// none came. A timeout is an answer here - "the ECU did not reply" - not an error.
    /// </summary>
    Task<CanFrame?> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}

/// <summary>
/// Which frames a reader keeps: those whose id, masked, equals <see cref="Id"/> masked. The default
/// mask compares every bit, so <c>new CanFilter(0x7E8)</c> is that id alone; a mask of 0x700 is a
/// range. <see cref="IsExtended"/> restricts the id format when set.
/// </summary>
public readonly record struct CanFilter(uint Id, uint Mask = CanFrame.MaxExtendedId, bool? IsExtended = null)
{
    public static CanFilter All { get; } = new(0, 0);

    public bool Matches(CanFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return (frame.Id & Mask) == (Id & Mask) && (IsExtended is null || IsExtended == frame.IsExtended);
    }
}

/// <summary>
/// One CAN or CAN FD frame, checked when made: an 11-bit id unless <see cref="IsExtended"/>, at
/// most 8 data bytes for classic CAN, and for CAN FD only the lengths a DLC can encode (0-8, 12,
/// 16, 20, 24, 32, 48, 64). A frame that could never be on the bus is refused rather than truncated.
/// </summary>
public sealed class CanFrame
{
    public const uint MaxStandardId = 0x7FF;
    public const uint MaxExtendedId = 0x1FFF_FFFF;

    private static readonly int[] FdLengths = [0, 1, 2, 3, 4, 5, 6, 7, 8, 12, 16, 20, 24, 32, 48, 64];

    private readonly byte[] _data;

    public CanFrame(
        uint id,
        ReadOnlySpan<byte> data,
        bool isExtended = false,
        bool isFd = false,
        bool bitRateSwitch = false,
        DateTimeOffset? timestamp = null)
    {
        if (id > (isExtended ? MaxExtendedId : MaxStandardId))
        {
            throw new ArgumentOutOfRangeException(nameof(id),
                $"CAN id 0x{id:X} does not fit a{(isExtended ? " 29-bit extended" : "n 11-bit standard")} identifier.");
        }

        if (isFd ? Array.IndexOf(FdLengths, data.Length) < 0 : data.Length > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(data),
                isFd
                    ? $"A CAN FD frame carries 0-8, 12, 16, 20, 24, 32, 48 or 64 bytes, not {data.Length}."
                    : $"A classic CAN frame carries at most 8 bytes, not {data.Length}.");
        }

        if (bitRateSwitch && !isFd)
        {
            throw new ArgumentException("Bit-rate switching exists only for CAN FD frames.", nameof(bitRateSwitch));
        }

        Id = id;
        IsExtended = isExtended;
        IsFd = isFd;
        BitRateSwitch = bitRateSwitch;
        Timestamp = timestamp;
        _data = data.ToArray();
    }

    public uint Id { get; }

    public bool IsExtended { get; }

    public bool IsFd { get; }

    public bool BitRateSwitch { get; }

    /// <summary>When the adapter saw the frame; null on a frame made to be sent.</summary>
    public DateTimeOffset? Timestamp { get; }

    public ReadOnlyMemory<byte> Data => _data;

    /// <summary>The data as space-separated hex bytes, "02 10 03" - the form steps read and write.</summary>
    public string DataHex => string.Join(' ', _data.Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));

    public CanFrame WithTimestamp(DateTimeOffset timestamp) => new(Id, _data, IsExtended, IsFd, BitRateSwitch, timestamp);

    public override string ToString() => $"0x{Id:X} [{_data.Length}] {DataHex}".TrimEnd();

    /// <summary>
    /// Bytes from the hex a sequence writes: "02 10 03", "021003", "02-10-03" or "0x02 0x10 0x03".
    /// An odd digit count or a character that is not hex is an error naming the text.
    /// </summary>
    public static byte[] ParseHex(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var digits = string.Concat(text
            .Split([' ', ',', '-', ':', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? part[2..] : part));
        if (digits.Length % 2 != 0 || !digits.All(Uri.IsHexDigit))
        {
            throw new FormatException($"'{text}' is not a sequence of hex bytes such as \"02 10 03\".");
        }

        return Convert.FromHexString(digits);
    }

    /// <summary>An id as a sequence writes it: "0x7E0", "7E0h", or decimal "2016".</summary>
    public static uint ParseId(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(trimmed[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
        {
            return hex;
        }

        if (trimmed.EndsWith('h') || trimmed.EndsWith('H'))
        {
            return uint.TryParse(trimmed[..^1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var suffixed)
                ? suffixed
                : throw new FormatException($"'{text}' is not a CAN id.");
        }

        return uint.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new FormatException($"'{text}' is not a CAN id; write it as 0x7E0 or as a decimal number.");
    }
}
