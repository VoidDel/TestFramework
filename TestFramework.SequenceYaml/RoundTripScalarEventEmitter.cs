using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.EventEmitters;

namespace TestFramework.SequenceYaml;

/// <summary>
/// Keeps a scalar's type stable across save and load, which the editor promises when it refuses
/// comments and anchors:
///
/// - String values that YAML would read back as null ("null", "~", "" and case variants) are
///   quoted. The serializer's own necessary-string quoting covers numbers and booleans; these are
///   forced here as well so the guarantee does not depend on its exact rule set.
/// - Floating-point values that happen to be whole are written with a fraction ("5.0", not "5"),
///   because an unquoted "5" reads back as an integer. Numeric comparisons would survive that, but
///   a value handed to a plugin as a double would arrive as an int after one round trip.
///
/// Registered innermost, after the type-assigning emitter, so that emitter cannot overwrite the
/// rendered value or style chosen here.
/// </summary>
internal sealed class RoundTripScalarEventEmitter : ChainedEventEmitter
{
    public RoundTripScalarEventEmitter(IEventEmitter nextEmitter)
        : base(nextEmitter)
    {
    }

    public override void Emit(ScalarEventInfo eventInfo, IEmitter emitter)
    {
        switch (eventInfo.Source.Value)
        {
            case string value when IsNullLike(value):
                eventInfo.Style = ScalarStyle.SingleQuoted;
                break;
            case double value when double.IsFinite(value):
                eventInfo.RenderedValue = WithFraction(value.ToString("R", CultureInfo.InvariantCulture));
                break;
            case float value when float.IsFinite(value):
                eventInfo.RenderedValue = WithFraction(value.ToString("R", CultureInfo.InvariantCulture));
                break;
        }

        base.Emit(eventInfo, emitter);
    }

    internal static bool IsNullLike(string value)
    {
        return value.Length == 0
            || value == "~"
            || value.Equals("null", StringComparison.OrdinalIgnoreCase);
    }

    private static string WithFraction(string rendered)
    {
        return rendered.Contains('.') || rendered.Contains('E') || rendered.Contains('e')
            ? rendered
            : rendered + ".0";
    }
}
