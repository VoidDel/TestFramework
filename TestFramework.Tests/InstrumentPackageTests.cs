using TestFramework.Instruments;
using Xunit;

namespace TestFramework.Tests;

/// <summary>
/// The parts of TestFramework.Instruments that are code rather than declarations: CAN frames and
/// filters, the reader dispatcher drivers build on, and the waveform measurements.
/// </summary>
public sealed class InstrumentPackageTests
{
    [Fact]
    public void CanFrame_RefusesAFrameThatCouldNeverBeOnTheBus()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CanFrame(0x800, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CanFrame(0x2000_0000, [], isExtended: true));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CanFrame(0x100, new byte[9]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CanFrame(0x100, new byte[10], isFd: true));
        Assert.Throws<ArgumentException>(() => new CanFrame(0x100, [], bitRateSwitch: true));

        Assert.Equal(64, new CanFrame(0x100, new byte[64], isFd: true).Data.Length);
        Assert.Equal(0x18FF50E5u, new CanFrame(0x18FF50E5, [], isExtended: true).Id);
    }

    [Theory]
    [InlineData("02 10 03")]
    [InlineData("021003")]
    [InlineData("02-10-03")]
    [InlineData("0x02 0x10 0x03")]
    public void CanFrame_ParsesHexInTheFormsSequencesWrite(string text)
    {
        Assert.Equal(new byte[] { 0x02, 0x10, 0x03 }, CanFrame.ParseHex(text));
        Assert.Equal("02 10 03", new CanFrame(0x7E0, CanFrame.ParseHex(text)).DataHex);
    }

    [Theory]
    [InlineData("0x7E0", 0x7E0u)]
    [InlineData("7E0h", 0x7E0u)]
    [InlineData("2016", 0x7E0u)]
    public void CanFrame_ParsesIds(string text, uint expected)
    {
        Assert.Equal(expected, CanFrame.ParseId(text));
    }

    [Fact]
    public void CanFrame_RefusesTextThatIsNotHex()
    {
        Assert.Throws<FormatException>(() => CanFrame.ParseHex("02 1"));
        Assert.Throws<FormatException>(() => CanFrame.ParseHex("zz"));
        Assert.Throws<FormatException>(() => CanFrame.ParseId("0xZZ"));
    }

    [Fact]
    public void CanFilter_ComparesTheMaskedBitsAndOptionallyTheFormat()
    {
        var frame = new CanFrame(0x7E8, []);

        Assert.True(new CanFilter(0x7E8).Matches(frame));
        Assert.False(new CanFilter(0x7E0).Matches(frame));
        Assert.True(new CanFilter(0x700, 0x700).Matches(frame));
        Assert.False(new CanFilter(0x7E8, IsExtended: true).Matches(frame));
        Assert.True(CanFilter.All.Matches(frame));
    }

    [Fact]
    public async Task Dispatcher_ReaderKeepsWhatArrivesAfterItOpens_AndNothingBefore()
    {
        var dispatcher = new CanFrameDispatcher();
        dispatcher.Publish(new CanFrame(0x7E8, [0x01]));

        using var reader = dispatcher.OpenReader(new CanFilter(0x7E8));
        dispatcher.Publish(new CanFrame(0x123, [0x02]));
        dispatcher.Publish(new CanFrame(0x7E8, [0x03]));

        var frame = await reader.ReadAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("03", frame!.DataHex);
        Assert.Null(await reader.ReadAsync(TimeSpan.FromMilliseconds(20)));
    }

    [Fact]
    public async Task Dispatcher_ReadersAreIndependent_AndADisposedOneHearsNothing()
    {
        var dispatcher = new CanFrameDispatcher();
        using var first = dispatcher.OpenReader();
        using var second = dispatcher.OpenReader();
        var closed = dispatcher.OpenReader();
        closed.Dispose();

        dispatcher.Publish(new CanFrame(0x100, [0xAA]));

        Assert.Equal("AA", (await first.ReadAsync(TimeSpan.FromSeconds(1)))!.DataHex);
        Assert.Equal("AA", (await second.ReadAsync(TimeSpan.FromSeconds(1)))!.DataHex);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => closed.ReadAsync(TimeSpan.FromMilliseconds(10)));
    }

    [Fact]
    public async Task Dispatcher_AFullReaderDropsTheOldest_AndCountsThem()
    {
        var dispatcher = new CanFrameDispatcher();
        using var reader = (CanFrameDispatcher.Reader)dispatcher.OpenReader(capacity: 2);

        for (byte value = 1; value <= 5; value++)
        {
            dispatcher.Publish(new CanFrame(0x100, [value]));
        }

        Assert.Equal(3, reader.DroppedFrames);
        Assert.Equal("04", (await reader.ReadAsync(TimeSpan.FromSeconds(1)))!.DataHex);
        Assert.Equal("05", (await reader.ReadAsync(TimeSpan.FromSeconds(1)))!.DataHex);
    }

    [Fact]
    public async Task Dispatcher_CancellationIsNotATimeout()
    {
        var dispatcher = new CanFrameDispatcher();
        using var reader = dispatcher.OpenReader();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(TimeSpan.FromSeconds(5), cancellation.Token));
    }

    [Fact]
    public void WaveformMath_MeasuresASquareWave()
    {
        // 1 kHz, 30 % duty, 0 to 5 V, sampled at 1 MHz for 10 ms.
        var record = Square(frequency: 1000, duty: 0.3, low: 0, high: 5, sampleRate: 1_000_000, samples: 10_000);

        Assert.Equal(1000, WaveformMath.Measure(record, ScopeMeasurement.Frequency), precision: 6);
        Assert.Equal(0.001, WaveformMath.Measure(record, ScopeMeasurement.Period), precision: 9);
        Assert.Equal(30, WaveformMath.Measure(record, ScopeMeasurement.DutyCycle), precision: 6);
        Assert.Equal(0.0003, WaveformMath.Measure(record, ScopeMeasurement.PositiveWidth), precision: 9);
        Assert.Equal(5, WaveformMath.Measure(record, ScopeMeasurement.PeakToPeak), precision: 9);
        Assert.Equal(1.5, WaveformMath.Measure(record, ScopeMeasurement.Mean), precision: 3);
        // An ideal edge sampled once per microsecond crosses 10 % to 90 % in 0.8 of a sample.
        Assert.Equal(0.8e-6, WaveformMath.Measure(record, ScopeMeasurement.RiseTime), precision: 12);
        Assert.Equal(0.8e-6, WaveformMath.Measure(record, ScopeMeasurement.FallTime), precision: 12);
    }

    [Fact]
    public void WaveformMath_MeasuresASineWave()
    {
        var samples = Enumerable.Range(0, 20_000).Select(index => 2.0 * Math.Sin(2 * Math.PI * 50 * index / 100_000.0)).ToArray();
        var record = new WaveformRecord("CH1", samples, 1.0 / 100_000);

        Assert.Equal(50, WaveformMath.Measure(record, ScopeMeasurement.Frequency), precision: 3);
        Assert.Equal(2 / Math.Sqrt(2), WaveformMath.Measure(record, ScopeMeasurement.Rms), precision: 3);
        Assert.Equal(4, WaveformMath.Measure(record, ScopeMeasurement.PeakToPeak), precision: 3);
    }

    [Fact]
    public void WaveformMath_IgnoresNoiseRidingOnTheLevels()
    {
        // Noise of 2 % of the swing crosses the midpoint nowhere, but would cross a level placed on
        // a flat top many times; the hysteresis band is what keeps those from counting as edges.
        var clean = Square(frequency: 1000, duty: 0.5, low: 0, high: 5, sampleRate: 1_000_000, samples: 5_000);
        var noisy = clean.Samples.Select((value, index) => value + (index % 2 == 0 ? 0.05 : -0.05)).ToArray();

        Assert.Equal(1000, WaveformMath.Measure(clean with { Samples = noisy }, ScopeMeasurement.Frequency), precision: 3);
    }

    [Fact]
    public void WaveformMath_RefusesWhatTheRecordCannotSupport()
    {
        var flat = new WaveformRecord("CH1", Enumerable.Repeat(3.3, 100).ToArray(), 1e-6);

        Assert.Equal(3.3, WaveformMath.Measure(flat, ScopeMeasurement.Mean), precision: 9);
        var period = Assert.Throws<InvalidOperationException>(() => WaveformMath.Measure(flat, ScopeMeasurement.Frequency));
        Assert.Contains("two", period.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => WaveformMath.Measure(flat, ScopeMeasurement.RiseTime));
        Assert.Throws<InvalidOperationException>(() => WaveformMath.Measure(new WaveformRecord("CH1", [], 1e-6), ScopeMeasurement.Mean));
    }

    [Fact]
    public void Units_AreTheOnesTheFrameworksTableKnows()
    {
        foreach (var function in Enum.GetValues<MeasurementFunction>())
        {
            Assert.True(TestFramework.Abstractions.Models.MeasurementUnits.IsKnown(MeasurementFunctions.UnitOf(function)), function.ToString());
        }

        foreach (var measurement in Enum.GetValues<ScopeMeasurement>())
        {
            Assert.True(TestFramework.Abstractions.Models.MeasurementUnits.IsKnown(ScopeMeasurements.UnitOf(measurement)), measurement.ToString());
        }
    }

    private static WaveformRecord Square(double frequency, double duty, double low, double high, double sampleRate, int samples)
    {
        // Whole samples per period, so every edge falls on the same sample of its period.
        var period = (int)Math.Round(sampleRate / frequency);
        var values = new double[samples];
        for (var index = 0; index < samples; index++)
        {
            values[index] = index % period < duty * period ? high : low;
        }

        return new WaveformRecord("CH1", values, 1 / sampleRate);
    }
}
