using TestFramework.Plugin.Abstractions.UI;

namespace TestFramework.App.Services;

internal static class StepSettingsParameterMerger
{
    public static Dictionary<string, object?> Merge(
        IReadOnlyDictionary<string, object?> current,
        IReadOnlyDictionary<string, object?> saved,
        IReadOnlyDictionary<string, object?> baseline)
    {
        var merged = new Dictionary<string, object?>(current, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in saved)
        {
            if (ShouldKeepExistingValue(current, baseline, key, value, out var existing))
            {
                merged[key] = existing;
                continue;
            }

            merged[key] = value;
        }

        return merged;
    }

    private static bool ShouldKeepExistingValue(
        IReadOnlyDictionary<string, object?> current,
        IReadOnlyDictionary<string, object?> baseline,
        string key,
        object? value,
        out object? existing)
    {
        existing = null;
        return current.TryGetValue(key, out existing) &&
               baseline.TryGetValue(key, out var baselineValue) &&
               SettingsValueConverter.ValuesEqual(value, baselineValue) &&
               (SettingsValueConverter.IsVariableReference(existing) ||
                !SettingsValueConverter.ValuesEqual(existing, baselineValue));
    }
}
