namespace TestFramework.Plugin.Abstractions.UI;

public interface IParameterBindingEditContext : ISettingsEditContext
{
    IReadOnlyDictionary<string, object?> Variables { get; }

    object? GetParameterValue(string key);

    void SetParameterValue(string key, object? value);
}
