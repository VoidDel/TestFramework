namespace TestFramework.Instruments;

/// <summary>
/// The <see cref="ScopeMeasurement"/>s computed from a captured record, for a driver whose
/// instrument does not measure on its own, a simulated scope, or a step that judges a waveform it
/// already holds.
///
/// Levels are taken from the record itself: the swing is its minimum to its maximum, the midpoint
/// halfway, and the 10 % / 90 % levels of edge times are of that swing. Crossings of the midpoint
/// need the signal to leave a band of 10 % of the swing on the far side before they count, so noise
/// riding on a flat top is not counted as edges. A measurement the record cannot support - a period
/// from fewer than two rising edges - is an <see cref="InvalidOperationException"/> naming why,
/// never a zero.
/// </summary>
public static class WaveformMath
{
    public static double Measure(WaveformRecord record, ScopeMeasurement measurement)
    {
        ArgumentNullException.ThrowIfNull(record);
        var samples = record.Samples;
        if (samples.Count == 0)
        {
            throw new InvalidOperationException($"Channel '{record.Channel}' has no samples to measure.");
        }

        return measurement switch
        {
            ScopeMeasurement.Mean => samples.Average(),
            ScopeMeasurement.Rms => Math.Sqrt(samples.Sum(value => value * value) / samples.Count),
            ScopeMeasurement.Maximum => samples.Max(),
            ScopeMeasurement.Minimum => samples.Min(),
            ScopeMeasurement.PeakToPeak => samples.Max() - samples.Min(),
            ScopeMeasurement.Period => Period(record),
            ScopeMeasurement.Frequency => 1.0 / Period(record),
            ScopeMeasurement.RiseTime => EdgeTime(record, rising: true),
            ScopeMeasurement.FallTime => EdgeTime(record, rising: false),
            ScopeMeasurement.PositiveWidth => PositiveWidth(record),
            ScopeMeasurement.DutyCycle => PositiveWidth(record) / Period(record) * 100.0,
            _ => throw new ArgumentOutOfRangeException(nameof(measurement), measurement, "Unknown scope measurement.")
        };
    }

    private static double Period(WaveformRecord record)
    {
        var (rising, _) = MidpointCrossings(record);
        if (rising.Count < 2)
        {
            throw new InvalidOperationException(
                $"Channel '{record.Channel}' has {rising.Count} rising edge(s) in the record; a period needs at least two.");
        }

        return (rising[^1] - rising[0]) / (rising.Count - 1);
    }

    private static double PositiveWidth(WaveformRecord record)
    {
        var (rising, falling) = MidpointCrossings(record);
        var widths = new List<double>();
        foreach (var up in rising)
        {
            var down = falling.FirstOrDefault(time => time > up, double.NaN);
            if (!double.IsNaN(down))
            {
                widths.Add(down - up);
            }
        }

        return widths.Count > 0
            ? widths.Average()
            : throw new InvalidOperationException($"Channel '{record.Channel}' has no complete positive pulse in the record.");
    }

    /// <summary>The times the signal crosses its midpoint going up and going down, with hysteresis.</summary>
    private static (List<double> Rising, List<double> Falling) MidpointCrossings(WaveformRecord record)
    {
        var samples = record.Samples;
        var rising = new List<double>();
        var falling = new List<double>();
        var (min, max) = (samples.Min(), samples.Max());
        if (max <= min)
        {
            return (rising, falling);
        }

        var mid = (min + max) / 2;
        var band = (max - min) * 0.1;

        // -1 below the band, +1 above it, 0 not yet known: the first crossing only counts once the
        // signal has been seen on the side it crosses from.
        var state = samples[0] > mid + band ? 1 : samples[0] < mid - band ? -1 : 0;
        double? lastUp = null, lastDown = null;
        for (var index = 1; index < samples.Count; index++)
        {
            if (samples[index - 1] < mid && samples[index] >= mid)
            {
                lastUp = CrossingTime(record, index, mid);
            }
            else if (samples[index - 1] >= mid && samples[index] < mid)
            {
                lastDown = CrossingTime(record, index, mid);
            }

            if (samples[index] > mid + band && state != 1)
            {
                if (state == -1 && lastUp is { } up)
                {
                    rising.Add(up);
                }

                state = 1;
            }
            else if (samples[index] < mid - band && state != -1)
            {
                if (state == 1 && lastDown is { } down)
                {
                    falling.Add(down);
                }

                state = -1;
            }
        }

        return (rising, falling);
    }

    /// <summary>10 % to 90 % of the swing for a rising edge, 90 % to 10 % for a falling one, averaged over the record.</summary>
    private static double EdgeTime(WaveformRecord record, bool rising)
    {
        var samples = record.Samples;
        var (min, max) = (samples.Min(), samples.Max());
        var low = min + (max - min) * 0.1;
        var high = min + (max - min) * 0.9;

        // Rising: armed below the low level, timed from its last crossing of the low level to its
        // crossing of the high one. Falling is the mirror image.
        var (start, end) = rising ? (low, high) : (high, low);
        var times = new List<double>();
        var armed = false;
        double? startTime = null;
        for (var index = 0; index < samples.Count; index++)
        {
            var value = samples[index];
            if (rising ? value < low : value > high)
            {
                armed = true;
                startTime = null;
                continue;
            }

            if (!armed || index == 0)
            {
                continue;
            }

            var previous = samples[index - 1];
            if (rising ? previous < start && value >= start : previous > start && value <= start)
            {
                startTime = CrossingTime(record, index, start);
            }

            if (startTime is { } from && (rising ? previous < end && value >= end : previous > end && value <= end))
            {
                times.Add(CrossingTime(record, index, end) - from);
                armed = false;
                startTime = null;
            }
        }

        return times.Count > 0
            ? times.Average()
            : throw new InvalidOperationException(
                $"Channel '{record.Channel}' has no complete {(rising ? "rising" : "falling")} edge in the record.");
    }

    /// <summary>When, between sample <paramref name="index"/> − 1 and <paramref name="index"/>, the signal passed <paramref name="level"/>.</summary>
    private static double CrossingTime(WaveformRecord record, int index, double level)
    {
        var before = record.Samples[index - 1];
        var after = record.Samples[index];
        var fraction = after == before ? 0 : (level - before) / (after - before);
        return record.StartSeconds + (index - 1 + fraction) * record.SampleIntervalSeconds;
    }
}
