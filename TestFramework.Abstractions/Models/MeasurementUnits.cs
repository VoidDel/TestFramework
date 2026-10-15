namespace TestFramework.Abstractions.Models;

/// <summary>
/// The units a numeric judgment can convert between, and what a host or a driver may add to them.
///
/// The table used to be a private list inside the runner, which made two things impossible: a
/// sequence could not be checked for a unit nobody knows until it ran and came out
/// <c>Inconclusive</c>, and a driver whose instrument reports in <c>Ah</c> could not teach the
/// framework that unit - it had to convert before returning, which is exactly the "the instrument
/// knows best" rule the unit fields exist to honour. The table lives beside the other rules the
/// validator, the runner and an editor share (<see cref="NumericComparisonRules"/>) and takes
/// registrations, so all three agree on what a unit means.
///
/// A unit is a symbol, a dimension and how to reach the dimension's base: <c>mV</c> is voltage,
/// scaled by 0.001; <c>K</c> is temperature, offset by -273.15 from the Celsius base. Conversion
/// is allowed only within one dimension; across dimensions, or to or from a symbol not in the
/// table, it refuses, and the judgment is <c>Inconclusive</c> rather than a comparison in the
/// wrong scale.
///
/// Symbols match case-sensitively first, because <c>mOhm</c> and <c>MOhm</c> are both real and a
/// thousand million apart. A symbol that matches nothing exactly matches case-insensitively when
/// that is unambiguous - <c>ohm</c> is <c>Ohm</c>, <c>hz</c> is <c>Hz</c> - so a hand-typed unit
/// still works, while <c>mohm</c> matches nothing rather than guessing.
/// </summary>
public static class MeasurementUnits
{
    private static readonly object Gate = new();

    private static volatile Dictionary<string, UnitDefinition> _units = BuiltIn();

    /// <summary>The symbols the table knows, as registered.</summary>
    public static IReadOnlyCollection<string> Known => _units.Keys.ToArray();

    /// <summary>True when <paramref name="unit"/> names a unit in the table; blank is "no unit", which is known.</summary>
    public static bool IsKnown(string? unit) => TryFind(unit, out _);

    /// <summary>The dimension of a unit - "voltage", "time" - or null when it is not in the table.</summary>
    public static string? DimensionOf(string? unit) => TryFind(unit, out var definition) ? definition.Dimension : null;

    /// <summary>
    /// The spelling the table uses for <paramref name="unit"/>: <c>Ω</c> is <c>Ohm</c>, <c>µ</c>
    /// and <c>μ</c> are <c>u</c>, <c>℃</c> is <c>°C</c>, and a case-insensitive match returns the
    /// registered case. An unknown unit comes back trimmed and otherwise as written.
    /// </summary>
    public static string Normalize(string? unit)
    {
        var spelled = Spell(unit);
        return TryFind(spelled, out var definition) ? definition.Symbol : spelled;
    }

    /// <summary>
    /// Converts <paramref name="value"/> from one unit to another. A blank target asks for no
    /// conversion; equal units need none; otherwise both must be known and of one dimension.
    /// </summary>
    public static bool TryConvert(double value, string? fromUnit, string? toUnit, out double converted)
    {
        converted = value;
        var source = Spell(fromUnit);
        var target = Spell(toUnit);
        if (target.Length == 0 || string.Equals(source, target, StringComparison.Ordinal))
        {
            return true;
        }

        if (!TryFind(source, out var from) || !TryFind(target, out var to) || from.Dimension != to.Dimension)
        {
            return false;
        }

        if (from.Symbol == to.Symbol)
        {
            return true;
        }

        var inBase = value * from.ScaleToBase + from.OffsetToBase;
        converted = (inBase - to.OffsetToBase) / to.ScaleToBase;
        return double.IsFinite(converted);
    }

    /// <summary>
    /// Adds a unit for the rest of the process: a driver that returns <c>"52.3 Ah"</c> registers
    /// <c>Ah</c> once, usually in its constructor, and every sequence may then write limits in it.
    ///
    /// <paramref name="scaleToBase"/> and <paramref name="offsetToBase"/> say how a value in this
    /// unit becomes one in the dimension's base: <c>base = value * scale + offset</c>. A dimension
    /// not yet in the table is created by its first unit, whose scale should then be 1.
    ///
    /// Registering a symbol the table already has is a no-op when the definition is the same and
    /// an error when it is not: two plugins must not be able to change what every sequence means
    /// by <c>mV</c>. The built-in symbols are protected the same way.
    /// </summary>
    public static void Register(string symbol, string dimension, double scaleToBase, double offsetToBase = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(dimension);
        if (!double.IsFinite(scaleToBase) || scaleToBase <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scaleToBase), "A unit's scale must be a positive, finite number.");
        }

        if (!double.IsFinite(offsetToBase))
        {
            throw new ArgumentOutOfRangeException(nameof(offsetToBase), "A unit's offset must be finite.");
        }

        var definition = new UnitDefinition(Spell(symbol), dimension.Trim(), scaleToBase, offsetToBase);
        lock (Gate)
        {
            if (_units.TryGetValue(definition.Symbol, out var existing))
            {
                if (existing == definition)
                {
                    return;
                }

                throw new InvalidOperationException(
                    $"Unit '{definition.Symbol}' is already defined as {existing.Dimension} (scale {existing.ScaleToBase}, offset {existing.OffsetToBase}) " +
                    $"and cannot be redefined as {definition.Dimension} (scale {definition.ScaleToBase}, offset {definition.OffsetToBase}).");
            }

            // Copy on write: readers hold the previous dictionary and never see a partial table.
            var units = new Dictionary<string, UnitDefinition>(_units, StringComparer.Ordinal) { [definition.Symbol] = definition };
            _units = units;
        }
    }

    private static bool TryFind(string? unit, out UnitDefinition definition)
    {
        var symbol = Spell(unit);
        var units = _units;
        if (units.TryGetValue(symbol, out definition!))
        {
            return true;
        }

        // Case-insensitive only when it cannot be two different units; a reader who typed "mohm"
        // may mean either, and a silent choice would be a comparison in the wrong scale.
        UnitDefinition? match = null;
        foreach (var candidate in units.Values)
        {
            if (!string.Equals(candidate.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (match is not null)
            {
                definition = default!;
                return false;
            }

            match = candidate;
        }

        definition = match!;
        return match is not null;
    }

    /// <summary>
    /// The symbol as the table spells it before lookup. Instruments and keyboards produce several
    /// code points for the same sign: U+00B5 MICRO SIGN and U+03BC GREEK SMALL LETTER MU for
    /// micro, U+2103 DEGREE CELSIUS for the two-character °C, and Ω for Ohm.
    /// </summary>
    private static string Spell(string? unit) =>
        unit?.Trim()
            .Replace("Ω", "Ohm", StringComparison.Ordinal)
            .Replace("μ", "u", StringComparison.Ordinal)
            .Replace("µ", "u", StringComparison.Ordinal)
            .Replace("℃", "°C", StringComparison.Ordinal)
        ?? string.Empty;

    private static Dictionary<string, UnitDefinition> BuiltIn()
    {
        var units = new Dictionary<string, UnitDefinition>(StringComparer.Ordinal);
        void Add(string symbol, string dimension, double scale, double offset = 0) =>
            units[symbol] = new UnitDefinition(symbol, dimension, scale, offset);

        Add("", "", 1.0);

        Add("V", "voltage", 1.0);
        Add("kV", "voltage", 1000.0);
        Add("mV", "voltage", 0.001);
        Add("uV", "voltage", 0.000001);

        Add("A", "current", 1.0);
        Add("kA", "current", 1000.0);
        Add("mA", "current", 0.001);
        Add("uA", "current", 0.000001);

        Add("Ohm", "resistance", 1.0);
        Add("MOhm", "resistance", 1000000.0);
        Add("kOhm", "resistance", 1000.0);
        Add("mOhm", "resistance", 0.001);
        Add("uOhm", "resistance", 0.000001);

        Add("Hz", "frequency", 1.0);
        Add("kHz", "frequency", 1000.0);
        Add("MHz", "frequency", 1000000.0);

        Add("s", "time", 1.0);
        Add("h", "time", 3600.0);
        Add("min", "time", 60.0);
        Add("ms", "time", 0.001);
        Add("us", "time", 0.000001);
        Add("ns", "time", 0.000000001);

        // Celsius is the base, so a Celsius reading converts to Celsius limits without arithmetic.
        // Plain "C" stays Celsius, as it always was here; the coulomb is not in the table.
        Add("C", "temperature", 1.0);
        Add("°C", "temperature", 1.0);
        Add("degC", "temperature", 1.0);
        Add("K", "temperature", 1.0, -273.15);

        Add("Ah", "charge", 1.0);
        Add("mAh", "charge", 0.001);

        Add("W", "power", 1.0);
        Add("kW", "power", 1000.0);
        Add("mW", "power", 0.001);

        Add("Wh", "energy", 1.0);
        Add("kWh", "energy", 1000.0);
        Add("mWh", "energy", 0.001);
        Add("J", "energy", 1.0 / 3600.0);
        Add("kJ", "energy", 1000.0 / 3600.0);

        Add("F", "capacitance", 1.0);
        Add("mF", "capacitance", 0.001);
        Add("uF", "capacitance", 0.000001);
        Add("nF", "capacitance", 0.000000001);
        Add("pF", "capacitance", 0.000000000001);

        Add("%", "ratio", 1.0);
        Add("ppm", "ratio", 0.0001);

        return units;
    }

    private sealed record UnitDefinition(string Symbol, string Dimension, double ScaleToBase, double OffsetToBase);
}
