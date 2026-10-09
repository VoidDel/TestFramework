using System.Collections;

namespace TestFramework.Abstractions.Models;

public sealed class TestStepDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "New Step";

    public string PluginId { get; set; } = string.Empty;

    public string PluginVersion { get; set; } = "1.0.0";

    public bool Enabled { get; set; } = true;

    public int? TimeoutMs { get; set; }

    public ErrorHandlingMode OnError { get; set; } = ErrorHandlingMode.Stop;

    /// <summary>
    /// A condition that must be true for the step to run, evaluated just before it would; false
    /// records the step as skipped. Null always runs.
    /// </summary>
    public string? RunIf { get; set; }

    /// <summary>Runs the step again on Error or Fail, or until a condition holds; see <see cref="RetryDefinition"/>.</summary>
    public RetryDefinition? Retry { get; set; }

    /// <summary>
    /// Resource aliases this step needs to itself while it runs - the DMM behind a switch matrix
    /// that four stations share. The runner holds each one exclusively for the step's duration, so
    /// another station's step naming the same alias waits, and the plugin never has to know the
    /// instrument is shared. Waiting counts against the step's timeout.
    /// </summary>
    public List<string> Exclusive { get; set; } = [];

    public Dictionary<string, object?> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<VariableWriteDefinition> VariableWrites { get; set; } = [];

    public TestStepDefinition Clone()
    {
        return new TestStepDefinition
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = Name + " Copy",
            PluginId = PluginId,
            PluginVersion = PluginVersion,
            Enabled = Enabled,
            TimeoutMs = TimeoutMs,
            OnError = OnError,
            RunIf = RunIf,
            Retry = Retry?.Clone(),
            Exclusive = [.. Exclusive],
            Parameters = Parameters.ToDictionary(
                pair => pair.Key,
                pair => CloneValue(pair.Value),
                StringComparer.OrdinalIgnoreCase),
            VariableWrites = VariableWrites.Select(write => new VariableWriteDefinition
            {
                Name = write.Name,
                OutputKey = write.OutputKey,
                Value = CloneValue(write.Value),
                WriteOnError = write.WriteOnError
            }).ToList()
        };
    }

    private static object? CloneValue(object? value)
    {
        if (value is null || value is string || value.GetType().IsValueType)
        {
            return value;
        }

        if (value is IDictionary dictionary)
        {
            // Case-sensitive: a dictionary inside a parameter value is the plugin's data, and
            // duplicating a step must not quietly drop one of two keys that differ only by case.
            // See VariableValue.
            var comparer = dictionary is IDictionary<string, object?> typed
                ? VariableValue.ComparerOf(typed)
                : StringComparer.Ordinal;

            var clone = new Dictionary<string, object?>(comparer);
            foreach (DictionaryEntry entry in dictionary)
            {
                clone[Convert.ToString(entry.Key) ?? string.Empty] = CloneValue(entry.Value);
            }

            return clone;
        }

        if (value is IEnumerable enumerable)
        {
            return enumerable.Cast<object?>().Select(CloneValue).ToList();
        }

        throw new InvalidOperationException($"Parameter value type '{value.GetType().FullName}' cannot be cloned safely.");
    }
}
