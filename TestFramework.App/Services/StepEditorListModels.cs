using TestFramework.Abstractions.Models;

namespace TestFramework.App.Services;

public sealed class ParameterEntry
{
    public required string Key { get; init; }

    public required string ValueText { get; init; }
}

public sealed class VariableWriteEntry
{
    public required VariableWriteDefinition Definition { get; init; }

    public required string Name { get; init; }

    public required string SourceText { get; init; }
}

internal static class StepEditorListModels
{
    public static IReadOnlyList<ParameterEntry> CreateParameterEntries(
        IReadOnlyDictionary<string, object?> parameters)
    {
        return parameters
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new ParameterEntry
            {
                Key = pair.Key,
                ValueText = EditorValueConverter.Format(pair.Value)
            })
            .ToList();
    }

    public static IReadOnlyList<VariableWriteEntry> CreateVariableWriteEntries(
        IEnumerable<VariableWriteDefinition> writes)
    {
        return writes
            .Select(write => new VariableWriteEntry
            {
                Definition = write,
                Name = string.IsNullOrWhiteSpace(write.Name) ? "（未命名）" : write.Name,
                SourceText = string.IsNullOrWhiteSpace(write.OutputKey)
                    ? $"固定值：{EditorValueConverter.Format(write.Value)}"
                    : $"输出：{write.OutputKey}"
            })
            .ToList();
    }
}
