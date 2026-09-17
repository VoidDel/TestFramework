using System.Globalization;
using Avalonia.Controls;
using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Plugins;
using TestFramework.App.Services;

using TestFramework.Plugin.Abstractions.UI;

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
    private bool _timeoutInputValid = true;

    public sealed class ErrorHandlingOption
    {
        public required ErrorHandlingMode Mode { get; init; }

        public required string DisplayName { get; init; }
    }

    public StepEditorWindow()
    {
        InitializeComponent();
        Closing += (_, e) => { if (!_timeoutInputValid) e.Cancel = true; };
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

        if (_pluginRegistry is null || !_pluginRegistry.TryGet(_step.PluginId, _step.PluginVersion, out var plugin))
        {
            PluginSettingsContent.Content = new TextBlock { Text = $"未找到插件：{_step.PluginId}" };
            return;
        }

        try
        {
            // Variable bindings stay in the model; the editor uses defaults for their preview values.
            var previewParameters = new Dictionary<string, object?>(plugin.SaveSettings(plugin.CreateDefaultSettings()), StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in _step.Parameters)
            {
                if (!SettingsValueConverter.IsVariableReference(value)) previewParameters[key] = value;
            }
            _settings = plugin.LoadSettings(previewParameters);
            _settingsBaselineParameters = plugin.SaveSettings(_settings);
            _settingsPlugin = plugin;
        }
        catch (Exception ex)
        {
            PluginSettingsContent.Content = new TextBlock { Text = $"配置无效，请在参数列表中修正：{ex.Message}", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            return;
        }

        // The editor factory is plugin-supplied code. An exception here would otherwise escape a
        // constructor or a synchronous handler and terminate the process, so the step falls back to
        // the raw parameter list instead.
        try
        {
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
        catch (Exception ex)
        {
            PluginSettingsContent.Content = new TextBlock
            {
                Text = $"插件配置界面加载失败，请使用参数列表编辑：{ex.Message}",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            };
        }
    }

    private void SaveSelectedPluginSettings()
    {
        if (_step is null || _settingsPlugin is null || _settings is null)
        {
            return;
        }

        _step.Parameters = StepSettingsParameterMerger.Merge(
            _step.Parameters,
            _settingsPlugin.SaveSettings(_settings),
            _settingsBaselineParameters);
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

        IReadOnlyList<ParameterEntry> entries = _step is null
            ? []
            : StepEditorListModels.CreateParameterEntries(_step.Parameters);

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

        IReadOnlyList<VariableWriteEntry> entries = _step is null
            ? []
            : StepEditorListModels.CreateVariableWriteEntries(_step.VariableWrites);

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
        VariableWriteValueBox.Text = SettingsValueConverter.Format(_selectedVariableWrite?.Value);
        VariableWriteOnErrorBox.IsChecked = _selectedVariableWrite?.WriteOnError ?? false;

        _updating = wasUpdating;
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

        int? timeoutMs = null;
        if (!string.IsNullOrWhiteSpace(TimeoutBox.Text))
        {
            if (!int.TryParse(TimeoutBox.Text, out var timeout) || timeout <= 0)
            {
                _timeoutInputValid = false;
                DataValidationErrors.SetErrors(TimeoutBox, [new InvalidDataException("超时必须为正整数毫秒，或留空。")]);
                return;
            }
            timeoutMs = timeout;
        }
        _timeoutInputValid = true;
        TimeoutBox.SetValue(DataValidationErrors.ErrorsProperty, null);
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
            ? SettingsValueConverter.Format(value)
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

        _step.Parameters[_selectedParameterKey] = SettingsValueConverter.Parse(ParameterValueBox.Text);
    }

    private void AddParameter_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_step is null)
        {
            return;
        }

        var key = UniqueNameGenerator.Create(_step.Parameters.Keys, "parameter");
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
        VariableWriteValueBox.Text = SettingsValueConverter.Format(_selectedVariableWrite?.Value);
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
            ? SettingsValueConverter.Parse(VariableWriteValueBox.Text)
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
            Name = UniqueNameGenerator.Create(_step.VariableWrites.Select(item => item.Name), "result"),
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
