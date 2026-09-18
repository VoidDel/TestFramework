using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace TestFramework.SequenceYaml;

/// <summary>
/// Gives an unquoted scalar under an <c>object</c>-typed slot (variables, parameters, settings)
/// its YAML type: <c>true</c>/<c>false</c> become <see cref="bool"/>, integers become
/// <see cref="int"/> (or <see cref="long"/> when they do not fit), and anything with a fraction or
/// exponent becomes <see cref="double"/>. Everything else, and every quoted scalar, stays a string.
///
/// YamlDotNet's own unquoted-type inference is not used because it picks the narrowest type that
/// parses - <c>5.0</c> becomes a <c>float</c>, <c>10000</c> a <c>short</c> - which loses precision
/// on measurement limits and hands plugins numeric types they have no reason to expect. Four types
/// are a contract a plugin can code against.
/// </summary>
internal sealed partial class PlainScalarTypeResolver : INodeTypeResolver
{
    public bool Resolve(NodeEvent? nodeEvent, ref Type currentType)
    {
        if (currentType != typeof(object) || nodeEvent is not Scalar { Style: ScalarStyle.Plain } scalar)
        {
            return false;
        }

        var text = scalar.Value;
        if (text.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            currentType = typeof(bool);
            return true;
        }

        if (IntegerPattern().IsMatch(text))
        {
            if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
            {
                currentType = typeof(int);
                return true;
            }

            if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
            {
                currentType = typeof(long);
                return true;
            }

            return false;
        }

        if (FloatPattern().IsMatch(text) &&
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            currentType = typeof(double);
            return true;
        }

        return false;
    }

    [GeneratedRegex(@"^[-+]?\d+$")]
    private static partial Regex IntegerPattern();

    // Requires a fraction or an exponent; a bare integer is handled above.
    [GeneratedRegex(@"^[-+]?(?:\d+\.\d*|\.\d+|\d+(?=[eE]))(?:[eE][-+]?\d+)?$")]
    private static partial Regex FloatPattern();
}
