using System.Globalization;

namespace TestFramework.Abstractions.Models;

/// <summary>
/// What counts as a number when a value from a sequence file or a variable has to be one.
///
/// Shared by the parameter check and by verdict limits, in the validator and in the runner, so a
/// value one of them accepts is never one another refuses. A value typed into a text box arrives as
/// a string, and YAML gives back int, long or double depending on how the number was written;
/// refusing those would report errors on sequences that run correctly. Non-finite numbers are not
/// numbers here: no limit or parameter can mean anything by NaN.
/// </summary>
public static class NumericValue
{
    public static bool TryRead(object? value, out double number)
    {
        switch (value)
        {
            case double existing:
                number = existing;
                return double.IsFinite(existing);
            case float existing:
                number = existing;
                return float.IsFinite(existing);
            case int or long or short or byte or uint or ulong or ushort or sbyte:
                number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return true;
            case decimal existing:
                number = (double)existing;
                return true;
            case string text:
                return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                    && double.IsFinite(number);
            default:
                number = 0;
                return false;
        }
    }
}
