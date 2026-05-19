using System.Globalization;
using Avalonia.Controls;
using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Plugins;
using TestFramework.App.Services;

namespace TestFramework.App.Views;

public sealed partial class StepEditorWindow : Window
{
    private readonly IPluginRegistry? _pluginRegistry;
    private readonly PluginSettingsEditorRegistry? _settingsEditorRegistry;
    private readonly IReadOnlyDictionary<string, object?> _variables = new Dictionary<string, object?>();
    private TestStepDefinition? _step;
    private ITestStepPlugin? _settingsPlugin;
    private object? _settings;
    private IReadOnlyDictionary<string, object?> _settingsBaselineParameters =
        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    private string? _selectedParameterKey;
    private VariableWriteDefinition? _selectedVariableWrite;
    private bool _updating = true;

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

    public sealed class ErrorHandlingOption
    {
        public required ErrorHandlingMode Mode { get; init; }

        public required string DisplayName { get; init; }
    }

    public StepEditorWindow()
    {
        InitializeComponent();
        ErrorHandlingBox.ItemsSource = CreateErrorHandlingOptions();
    }

    public StepEditorWindow(
        TestStepDefinition step,
        IPluginRegistry pluginRegistry,
        PluginSettingsEditorRegistry settingsEditorRegistry,
        IReadOnlyDictionary<string, object?> variables)
        : this()
    {
        _step = step;
        _pluginRegistry = pluginRegistry;
        _settingsEditorRegistry = settingsEditorRegistry;
        _variables = variables;

        RefreshAll();
    }

    private static IReadOnlyList<ErrorHandlingOption> CreateErrorHandlingOptions()
    {
        return
        [
            new ErrorHandlingOption { Mode = ErrorHandlingMode.Stop, DisplayName = "停止序列" },
            new ErrorHandlingOption { Mode = ErrorHandlingMode.Continue, DisplayName = "继续执行" },
            new ErrorHandlingOption { Mode = ErrorHandlingMode.JumpToCleanup, DisplayName = "跳转清理" }
        ];
    }

    private static ErrorHandlingOption? FindErrorHandlingOption(ComboBox comboBox, ErrorHandlingMode mode)
    {
        return comboBox.ItemsSource?
            .OfType<ErrorHandlingOption>()
            .FirstOrDefault(option => option.Mode == mode);
    }

    private void RefreshAll()
    {
        _updating = true;

        Title = string.IsNullOrWhiteSpace(_step?.Name) ? "编辑 Step" : $"编辑 Step - {_step.Name}";
        StepNameBox.Text = _step?.Name ?? string.Empty;
        StepEnabledBox.IsChecked = _step?.Enabled ?? false;
        StepPluginBox.Text = _step?.PluginId ?? string.Empty;
        TimeoutBox.Text = _step?.TimeoutMs?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        ErrorHandlingBox.SelectedItem = FindErrorHandlingOption(
            ErrorHandlingBox,
            _step?.OnError ?? ErrorHandlingMode.Stop);

        RefreshPluginSettingsEditor();
        RefreshParameterList();
        RefreshVariableWritesList();

        _updating = false;
    }

    private void RefreshPluginSettingsEditor()
    {
        PluginSettingsContent.Content = null;
        _settingsPlugin = null;
        _settings = null;
        _settingsBaselineParameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (_step is null)
        {
            PluginSettingsContent.Content = new TextBlock { Text = "未选择 Step。" };
            return;
        }

        if (_pluginRegistry is null || !_pluginRegistry.TryGet(_step.PluginId, out var plugin))
        {
            PluginSettingsContent.Content = new TextBlock { Text = $"未找到插件：{_step.PluginId}" };
            return;
        }

        _settingsPlugin = plugin;
        _settings = plugin.LoadSettings(_step.Parameters);
        _settingsBaselineParameters = plugin.SaveSettings(_settings);

        if (_settingsEditorRegistry is not null &&
            _settingsEditorRegistry.TryCreateEditor(
                _step.PluginId,
                _step.PluginVersion,
                _settings,
                new SettingsEditContext(
                    SaveSelectedPluginSettings,
                    _variables,
                    GetStepParameterValue,
                    SetStepParameterValue),
                out var editor))
        {
            PluginSettingsContent.Content = editor;
        }
        else
        {
            PluginSettingsContent.Content = new TextBlock { Text = "该插件未提供配置界面。" };
        }
    }

    private void SaveSelectedPluginSettings()
    {
        if (_step is null || _settingsPlugin is null || _settings is null)
        {
            return;
        }

        var current = _step.Parameters;
        var saved = _settingsPlugin.SaveSettings(_settings);
        var merged = new Dictionary<string, object?>(current, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in saved)
        {
            if (current.TryGetValue(key, out var existing) &&
                _settingsBaselineParameters.TryGetValue(key, out var baseline) &&
                ValuesEqual(value, baseline) &&
                (IsVariableReference(existing) || !ValuesEqual(existing, baseline)))
            {
                merged[key] = existing;
                continue;
            }

            merged[key] = value;
        }

        _step.Parameters = merged;
        RefreshParameterList();
    }

    private object? GetStepParameterValue(string key)
    {
        return _step is not null && _step.Parameters.TryGetValue(key, out var value)
            ? value
            : null;
    }

    private void SetStepParameterValue(string key, object? value)
    {
        if (_step is null)
        {
            return;
        }

        _step.Parameters[key] = value;
        RefreshParameterList();
    }

    private void RefreshParameterList()
    {
        var wasUpdating = _updating;
        _updating = true;

        var entries = _step?.Parameters
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new ParameterEntry
            {
                Key = pair.Key,
                ValueText = FormatValue(pair.Value)
            })
            .ToList() ?? [];

        ParametersList.ItemsSource = null;
        ParametersList.ItemsSource = entries;

        var selected = entries.FirstOrDefault(entry =>
            string.Equals(entry.Key, _selectedParameterKey, StringComparison.OrdinalIgnoreCase));
        if (selected is null && entries.Count > 0 && _selectedParameterKey is not null)
        {
            selected = entries[0];
        }

        ParametersList.SelectedItem = selected;
        _selectedParameterKey = selected?.Key;
        ParameterKeyBox.Text = selected?.Key ?? string.Empty;
        ParameterValueBox.Text = selected?.ValueText ?? string.Empty;

        _updating = wasUpdating;
    }

    private void RefreshVariableWritesList()
    {
        var wasUpdating = _updating;
        _updating = true;

        var entries = _step?.VariableWrites
            .Select(write => new VariableWriteEntry
            {
                Definition = write,
                Name = string.IsNullOrWhiteSpace(write.Name) ? "（未命名）" : write.Name,
                SourceText = string.IsNullOrWhiteSpace(write.OutputKey)
                    ? $"固定值：{FormatValue(write.Value)}"
                    : $"输出：{write.OutputKey}"
            })
            .ToList() ?? [];

        VariableWritesList.ItemsSource = null;
        VariableWritesList.ItemsSource = entries;

        var selected = entries.FirstOrDefault(entry => ReferenceEquals(entry.Definition, _selectedVariableWrite));
        if (selected is null && entries.Count > 0 && _selectedVariableWrite is not null)
        {
            selected = entries[0];
        }

        VariableWritesList.SelectedItem = selected;
        _selectedVariableWrite = selected?.Definition;
        VariableWriteNameBox.Text = _selectedVariableWrite?.Name ?? string.Empty;
        VariableWriteOutputKeyBox.Text = _selectedVariableWrite?.OutputKey ?? string.Empty;
        VariableWriteValueBox.Text = FormatValue(_selectedVariableWrite?.Value);
        VariableWriteOnErrorBox.IsChecked = _selectedVariableWrite?.WriteOnError ?? false;

        _updating = wasUpdating;
    }

    private static string MakeUniqueName(IEnumerable<string> existingNames, string baseName)
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

    private static object? ParseEditorValue(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var trimmed = text.Trim();
        if (trimmed.Contains("${", StringComparison.Ordinal))
        {
            return text;
        }

        if (bool.TryParse(trimmed, out var boolean))
        {
            return boolean;
        }

        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            return integer;
        }

        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }

        return text;
    }

    private static string FormatValue(object? value)
    {
        return value switch
        {
            null => string.Empty,
            bool boolean => boolean.ToString(CultureInfo.InvariantCulture).ToLowerInvariant(),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
        };
    }

    private static bool IsVariableReference(object? value)
    {
        return value is string text && text.Contains("${", StringComparison.Ordinal);
    }

    private static bool ValuesEqual(object? left, object? right)
    {
        if (Equals(left, right))
        {
            return true;
        }

        return string.Equals(FormatValue(left), FormatValue(right), StringComparison.Ordinal);
    }

    private void StepNameBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_updating || _step is null)
        {
            return;
        }

        var text = StepNameBox.Text ?? string.Empty;
        if (string.Equals(_step.Name, text, StringComparison.Ordinal))
        {
            return;
        }

        _step.Name = text;
        Title = string.IsNullOrWhiteSpace(_step.Name) ? "编辑 Step" : $"编辑 Step - {_step.Name}";
    }

    private void StepEnabledBox_OnChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_updating || _step is null)
        {
            return;
        }

        _step.Enabled = StepEnabledBox.IsChecked == true;
    }

    private void TimeoutBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_updating || _step is null)
        {
            return;
        }

        int? timeoutMs = int.TryParse(TimeoutBox.Text, out var timeout) && timeout > 0 ? timeout : null;
        if (_step.TimeoutMs == timeoutMs)
        {
            return;
        }

        _step.TimeoutMs = timeoutMs;
    }

    private void ErrorHandlingBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating || _step is null || ErrorHandlingBox.SelectedItem is not ErrorHandlingOption option)
        {
            return;
        }

        _step.OnError = option.Mode;
    }

    private void ParametersList_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating)
        {
            return;
        }

        _selectedParameterKey = (ParametersList.SelectedItem as ParameterEntry)?.Key;
        var wasUpdating = _updating;
        _updating = true;
        ParameterKeyBox.Text = _selectedParameterKey ?? string.Empty;
        ParameterValueBox.Text = _step is not null &&
                                 _selectedParameterKey is not null &&
                                 _step.Parameters.TryGetValue(_selectedParameterKey, out var value)
            ? FormatValue(value)
            : string.Empty;
        _updating = wasUpdating;
    }

    private void ParameterKeyBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_updating || _step is null || string.IsNullOrWhiteSpace(_selectedParameterKey))
        {
            return;
        }

        var newKey = ParameterKeyBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(newKey) ||
            string.Equals(newKey, _selectedParameterKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_step.Parameters.ContainsKey(newKey))
        {
            RefreshParameterList();
            return;
        }

        var value = _step.Parameters[_selectedParameterKey];
        _step.Parameters.Remove(_selectedParameterKey);
        _step.Parameters[newKey] = value;
        _selectedParameterKey = newKey;
        RefreshParameterList();
    }

    private void ParameterValueBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_updating || _step is null || string.IsNullOrWhiteSpace(_selectedParameterKey))
        {
            return;
        }

        _step.Parameters[_selectedParameterKey] = ParseEditorValue(ParameterValueBox.Text);
    }

    private void AddParameter_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_step is null)
        {
            return;
        }

        var key = MakeUniqueName(_step.Parameters.Keys, "parameter");
        _step.Parameters[key] = string.Empty;
        _selectedParameterKey = key;
        RefreshParameterList();
    }

    private void DeleteParameter_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_step is null || string.IsNullOrWhiteSpace(_selectedParameterKey))
        {
            return;
        }

        _step.Parameters.Remove(_selectedParameterKey);
        _selectedParameterKey = null;
        RefreshParameterList();
    }

    private void VariableWritesList_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating)
        {
            return;
        }

        _selectedVariableWrite = (VariableWritesList.SelectedItem as VariableWriteEntry)?.Definition;
        var wasUpdating = _updating;
        _updating = true;
        VariableWriteNameBox.Text = _selectedVariableWrite?.Name ?? string.Empty;
        VariableWriteOutputKeyBox.Text = _selectedVariableWrite?.OutputKey ?? string.Empty;
        VariableWriteValueBox.Text = FormatValue(_selectedVariableWrite?.Value);
        VariableWriteOnErrorBox.IsChecked = _selectedVariableWrite?.WriteOnError ?? false;
        _updating = wasUpdating;
    }

    private void VariableWriteNameBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_updating || _selectedVariableWrite is null)
        {
            return;
        }

        _selectedVariableWrite.Name = VariableWriteNameBox.Text?.Trim() ?? string.Empty;
    }

    private void VariableWriteOutputKeyBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_updating || _selectedVariableWrite is null)
        {
            return;
        }

        _selectedVariableWrite.OutputKey = string.IsNullOrWhiteSpace(VariableWriteOutputKeyBox.Text)
            ? null
            : VariableWriteOutputKeyBox.Text.Trim();
    }

    private void VariableWriteValueBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_updating || _selectedVariableWrite is null)
        {
            return;
        }

        _selectedVariableWrite.Value = string.IsNullOrWhiteSpace(_selectedVariableWrite.OutputKey)
            ? ParseEditorValue(VariableWriteValueBox.Text)
            : null;
    }

    private void VariableWriteOnErrorBox_OnChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_updating || _selectedVariableWrite is null)
        {
            return;
        }

        _selectedVariableWrite.WriteOnError = VariableWriteOnErrorBox.IsChecked == true;
    }

    private void AddVariableWrite_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_step is null)
        {
            return;
        }

        var write = new VariableWriteDefinition
        {
            Name = MakeUniqueName(_step.VariableWrites.Select(item => item.Name), "result"),
            OutputKey = "value"
        };
        _step.VariableWrites.Add(write);
        _selectedVariableWrite = write;
        RefreshVariableWritesList();
    }

    private void DeleteVariableWrite_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_step is null || _selectedVariableWrite is null)
        {
            return;
        }

        _step.VariableWrites.Remove(_selectedVariableWrite);
        _selectedVariableWrite = null;
        RefreshVariableWritesList();
    }

    private void Close_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
    }
}
