namespace TestFramework.App.Services;

internal static class UniqueNameGenerator
{
    public static string Create(IEnumerable<string> existingNames, string baseName)
    {
        var existing = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(baseName))
        {
            return baseName;
        }

        for (var index = 1; ; index++)
        {
            var candidate = $"{baseName}{index}";
            if (!existing.Contains(candidate))
            {
                return candidate;
            }
        }
    }
}
