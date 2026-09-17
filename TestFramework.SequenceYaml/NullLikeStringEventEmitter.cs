using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.EventEmitters;

namespace TestFramework.SequenceYaml;

/// <summary>
/// Forces quoting for string values that YAML would otherwise resolve back to null
/// ("null", "~", "" and their case variants). Without this the visual editor silently
/// turns such values into null on the next load, which breaks the round-trip guarantee
/// the editor makes when it rejects comments and anchors.
/// </summary>
internal sealed class NullLikeStringEventEmitter : ChainedEventEmitter
{
    public NullLikeStringEventEmitter(IEventEmitter nextEmitter)
        : base(nextEmitter)
    {
    }

    public override void Emit(ScalarEventInfo eventInfo, IEmitter emitter)
    {
        if (eventInfo.Source.Value is string value && IsNullLike(value))
        {
            eventInfo.Style = ScalarStyle.SingleQuoted;
        }

        base.Emit(eventInfo, emitter);
    }

    internal static bool IsNullLike(string value)
    {
        return value.Length == 0
            || value == "~"
            || value.Equals("null", StringComparison.OrdinalIgnoreCase);
    }
}
