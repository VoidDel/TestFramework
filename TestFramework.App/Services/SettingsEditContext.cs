using TestFramework.Plugin.Abstractions.UI;

namespace TestFramework.App.Services;

internal sealed class SettingsEditContext : IParameterBindingEditContext
{
    private readonly Action _onChanged;
    private readonly Func<string, object?> _getParameterValue;
    private readonly Action<string, object?> _setParameterValue;

    public SettingsEditContext(
        Action onChanged,
        IReadOnlyDictionary<string, object?> variables,
        Func<string, object?> getParameterValue,
        Action<string, object?> setParameterValue)
    {
        _onChanged = onChanged;
        Variables = variables;
        _getParameterValue = getParameterValue;
        _setParameterValue = setParameterValue;
    }

    public IReadOnlyDictionary<string, object?> Variables { get; }

    public void NotifySettingsChanged()
    {
        _onChanged();
    }

    public object? GetParameterValue(string key)
    {
        return _getParameterValue(key);
    }

    public void SetParameterValue(string key, object? value)
    {
        _setParameterValue(key, value);
    }
}
