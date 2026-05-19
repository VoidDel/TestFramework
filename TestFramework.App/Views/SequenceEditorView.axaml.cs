using System.Globalization;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Plugins;
using TestFramework.App.Services;
using TestFramework.Core.Execution;
using TestFramework.Core.Plugins;
using TestFramework.Core.Resources;
using TestFramework.Plugin.Abstractions.UI;
using TestFramework.Plugins.BasicSteps;
using TestFramework.SequenceYaml;
using TestFramework.SequenceYaml.Validation;

namespace TestFramework.App.Views;

public sealed partial class SequenceEditorView : UserControl
{
    private static readonly FilePickerFileType YamlFileType = new("YAML")
    {
        Patterns = ["*.yaml", "*.yml"]
    };

    private readonly PluginRegistry _pluginRegistry = new();
    private readonly ResourcePluginRegistry _resourcePluginRegistry = new();
    private readonly PluginSettingsEditorRegistry _settingsEditorRegistry = new();
    private readonly TestSequenceYamlService _yamlService = new();
    private readonly TestSequenceValidator _validator;

    private TestSequence _sequence = new();
    private TestItemDefinition? _selectedItem;
    private TestStepDefinition? _selectedStep;
    private StepSection _selectedSection = StepSection.Init;
    private string? _currentFile;
    private string? _selectedVariableName;
    private bool _updating = true;
    private bool _clearingStepSelection;

    public sealed class VariableEntry
    {
        public required string Name { get; init; }

        public required string ValueText { get; init; }
    }

    public SequenceEditorView()
    {
        InitializeComponent();

        RegisterPlugins();
        RegisterSettingsEditors();
        _validator = new TestSequenceValidator(_pluginRegistry);

        PluginCombo.ItemsSource = _pluginRegistry.Plugins;
        PluginCombo.SelectedIndex = 0;

        _sequence = CreateDefaultSequence();
        RefreshAll();
    }

    private void RegisterPlugins()
    {
        _pluginRegistry.Register(new DelayStepPlugin());
        _pluginRegistry.Register(new LogStepPlugin());
        _pluginRegistry.Register(new LimitCheckStepPlugin());
        _pluginRegistry.Register(new ThrowStepPlugin());

        var pluginDirectory = Path.Combine(AppContext.BaseDirectory, "Plugins");
        var loader = new PluginLoader(_pluginRegistry);
        var report = loader.LoadFromDirectoryWithReport(pluginDirectory);
        RegisterSettingsEditors(report.LoadedPlugins);
        foreach (var failure in report.Failures)
        {
            AppendLog($"插件加载失败：{failure.AssemblyPath} - {failure.Message}");
        }

        var resourceReport = new ResourcePluginLoader(_resourcePluginRegistry).LoadFromDirectory(pluginDirectory);
        foreach (var failure in resourceReport.Failures)
        {
            AppendLog($"资源插件加载失败：{failure.AssemblyPath} - {failure.Message}");
        }
    }

    private void RegisterSettingsEditors()
    {
        BasicStepSettingsEditors.Register(_settingsEditorRegistry);
    }

    private void RegisterSettingsEditors(IEnumerable<ITestStepPlugin> plugins)
    {
        foreach (var plugin in plugins)
        {
            if (plugin is ITestStepSettingsEditorProvider editorProvider)
            {
                _settingsEditorRegistry.Register(
                    plugin.Descriptor.PluginId,
                    plugin.Descriptor.Version,
                    editorProvider.CreateEditor);
            }
        }
    }

    private TestSequence CreateDefaultSequence()
    {
        var logPlugin = _pluginRegistry.GetRequired("basic.log");
        var delayPlugin = _pluginRegistry.GetRequired("basic.delay");
        var checkPlugin = _pluginRegistry.GetRequired("basic.limit-check");

        var init = CreateStep(logPlugin, "初始化工装");
        var mainDelay = CreateStep(delayPlugin, "等待稳定");
        var mainCheck = CreateStep(checkPlugin, "检查电压");
        var cleanup = CreateStep(logPlugin, "清理工装");
        cleanup.OnError = ErrorHandlingMode.Continue;

        var item = new TestItemDefinition
        {
            Name = "电压测试",
            InitSteps = [init],
            MainSteps = [mainDelay, mainCheck],
            CleanupSteps = [cleanup],
            VerdictSource = new VerdictSource
            {
                StepId = mainCheck.Id,
                OutputKey = "verdict"
            }
        };

        return new TestSequence
        {
            Name = "示例测试序列",
            Variables =
            {
                ["dutSerial"] = string.Empty,
                ["targetVoltage"] = 5.0,
                ["voltageMin"] = 4.8,
                ["voltageMax"] = 5.2
            },
            Items = [item]
        };
    }

    private static TestStepDefinition CreateStep(ITestStepPlugin plugin, string name)
    {
        var settings = plugin.CreateDefaultSettings();
        return new TestStepDefinition
        {
            Name = name,
            PluginId = plugin.Descriptor.PluginId,
            PluginVersion = plugin.Descriptor.Version.ToString(),
            OnError = ErrorHandlingMode.Stop,
            Parameters = new Dictionary<string, object?>(plugin.SaveSettings(settings), StringComparer.OrdinalIgnoreCase)
        };
    }

    private void RefreshAll()
    {
        _updating = true;
        SequenceNameBox.Text = _sequence.Name;
        ItemsList.ItemsSource = null;
        ItemsList.ItemsSource = _sequence.Items;
        _selectedItem ??= _sequence.Items.FirstOrDefault();
        ItemsList.SelectedItem = _selectedItem;
        RefreshVariablesList();
        RefreshItemPanel();
        _updating = false;
    }

    private void RefreshItemPanel()
    {
        _updating = true;

        ItemNameBox.Text = _selectedItem?.Name ?? string.Empty;
        ItemEnabledBox.IsChecked = _selectedItem?.Enabled ?? false;

        RefreshStepLists();
        RefreshVerdictControls();

        _updating = false;
    }

    private void RefreshStepLists()
    {
        InitStepsList.ItemsSource = null;
        MainStepsList.ItemsSource = null;
        CleanupStepsList.ItemsSource = null;

        InitStepsList.ItemsSource = _selectedItem?.InitSteps;
        MainStepsList.ItemsSource = _selectedItem?.MainSteps;
        CleanupStepsList.ItemsSource = _selectedItem?.CleanupSteps;

        SelectStepInList();
    }

    private void RefreshVerdictControls()
    {
        VerdictStepCombo.ItemsSource = null;
        VerdictStepCombo.ItemsSource = _selectedItem?.MainSteps;

        if (_selectedItem is null)
        {
            VerdictStepCombo.SelectedItem = null;
            VerdictOutputBox.Text = string.Empty;
            return;
        }

        VerdictStepCombo.SelectedItem = _selectedItem.MainSteps.FirstOrDefault(step =>
            string.Equals(step.Id, _selectedItem.VerdictSource.StepId, StringComparison.OrdinalIgnoreCase));
        VerdictOutputBox.Text = _selectedItem.VerdictSource.OutputKey ?? string.Empty;
    }

    private void SelectStepInList()
    {
        _clearingStepSelection = true;
        InitStepsList.SelectedItem = null;
        MainStepsList.SelectedItem = null;
        CleanupStepsList.SelectedItem = null;

        if (_selectedStep is not null)
        {
            GetStepListBox(_selectedSection).SelectedItem = _selectedStep;
        }

        _clearingStepSelection = false;
    }

    private List<TestStepDefinition>? GetCurrentSteps()
    {
        return _selectedSection switch
        {
            StepSection.Init => _selectedItem?.InitSteps,
            StepSection.Main => _selectedItem?.MainSteps,
            StepSection.Cleanup => _selectedItem?.CleanupSteps,
            _ => null
        };
    }

    private ListBox GetStepListBox(StepSection section)
    {
        return section switch
        {
            StepSection.Init => InitStepsList,
            StepSection.Main => MainStepsList,
            StepSection.Cleanup => CleanupStepsList,
            _ => MainStepsList
        };
    }

    private void RefreshVariablesList()
    {
        var wasUpdating = _updating;
        _updating = true;

        var entries = _sequence.Variables
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new VariableEntry
            {
                Name = pair.Key,
                ValueText = FormatValue(pair.Value)
            })
            .ToList();
        VariablesList.ItemsSource = null;
        VariablesList.ItemsSource = entries;

        var selected = entries.FirstOrDefault(entry =>
            string.Equals(entry.Name, _selectedVariableName, StringComparison.OrdinalIgnoreCase));
        if (selected is null && entries.Count > 0 && _selectedVariableName is not null)
        {
            selected = entries[0];
        }

        VariablesList.SelectedItem = selected;
        _selectedVariableName = selected?.Name;
        VariableNameBox.Text = selected?.Name ?? string.Empty;
        VariableValueBox.Text = selected?.ValueText ?? string.Empty;

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

    private static string FormatVerdict(TestVerdict verdict)
    {
        return verdict switch
        {
            TestVerdict.None => "未运行",
            TestVerdict.Pass => "通过",
            TestVerdict.Fail => "失败",
            TestVerdict.Error => "错误",
            TestVerdict.Skipped => "跳过",
            TestVerdict.Inconclusive => "无结论",
            _ => verdict.ToString()
        };
    }

    private void AppendLog(string message)
    {
        LogBox.Text += $"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}";
        LogBox.CaretIndex = LogBox.Text?.Length ?? 0;
    }

    private bool ValidateForSaveOrRun()
    {
        var issues = _validator.Validate(_sequence);
        if (issues.Count == 0)
        {
            StatusText.Text = "校验通过。";
            return true;
        }

        StatusText.Text = $"校验失败：{issues.Count} 个问题。";
        AppendLog("校验失败：");
        foreach (var issue in issues)
        {
            AppendLog("  " + issue);
        }

        return false;
    }

    private async void New_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _currentFile = null;
        _selectedItem = null;
        _selectedStep = null;
        _selectedVariableName = null;
        _sequence = CreateDefaultSequence();
        RefreshAll();
        AppendLog("已新建测试序列。");
    }

    private async void Open_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider is null)
        {
            StatusText.Text = "无法访问文件选择器。";
            return;
        }

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "打开测试序列",
            AllowMultiple = false,
            FileTypeFilter = [YamlFileType]
        });

        var file = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(file))
        {
            return;
        }

        _sequence = await _yamlService.LoadFromFileAsync(file);
        _currentFile = file;
        _selectedItem = _sequence.Items.FirstOrDefault();
        _selectedStep = null;
        _selectedVariableName = null;
        RefreshAll();
        AppendLog($"已打开：{file}");
    }

    private async void Save_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentFile))
        {
            await SaveAsAsync();
            return;
        }

        if (!ValidateForSaveOrRun())
        {
            return;
        }

        await _yamlService.SaveToFileAsync(_sequence, _currentFile);
        AppendLog($"已保存：{_currentFile}");
    }

    private async void SaveAs_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await SaveAsAsync();
    }

    private async Task SaveAsAsync()
    {
        if (!ValidateForSaveOrRun())
        {
            return;
        }

        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider is null)
        {
            StatusText.Text = "无法访问文件选择器。";
            return;
        }

        var file = (await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存测试序列",
            SuggestedFileName = "测试序列.yaml",
            DefaultExtension = "yaml",
            FileTypeChoices = [YamlFileType]
        }))?.TryGetLocalPath();

        if (string.IsNullOrWhiteSpace(file))
        {
            return;
        }

        _currentFile = file;
        await _yamlService.SaveToFileAsync(_sequence, file);
        AppendLog($"已保存：{file}");
    }

    private async void Run_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!ValidateForSaveOrRun())
        {
            return;
        }

        LogBox.Text = string.Empty;
        try
        {
            var resources = await new RuntimeResourceBuilder(_resourcePluginRegistry).BuildAsync(_sequence);
            var runner = new TestSequenceRunner(_pluginRegistry, new UiExecutionObserver(AppendLog), resources);
            var result = await runner.RunAsync(_sequence);
            StatusText.Text = $"运行完成：{FormatVerdict(result.Verdict)}";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "运行已取消。";
            AppendLog("运行已取消。");
        }
        catch (Exception ex)
        {
            StatusText.Text = "运行失败。";
            AppendLog("运行失败：" + ex.Message);
        }
    }

    private void AddItem_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var item = new TestItemDefinition { Name = "新测试项" };
        var plugin = _pluginRegistry.GetRequired("basic.limit-check");
        var main = CreateStep(plugin, "主检查");
        item.MainSteps.Add(main);
        item.VerdictSource.StepId = main.Id;
        item.VerdictSource.OutputKey = "verdict";

        _sequence.Items.Add(item);
        _selectedItem = item;
        _selectedStep = null;
        RefreshAll();
    }

    private void DeleteItem_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selectedItem is null)
        {
            return;
        }

        var index = _sequence.Items.IndexOf(_selectedItem);
        _sequence.Items.Remove(_selectedItem);
        _selectedItem = _sequence.Items.ElementAtOrDefault(Math.Clamp(index, 0, Math.Max(0, _sequence.Items.Count - 1)));
        _selectedStep = null;
        RefreshAll();
    }

    private void VariablesList_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating)
        {
            return;
        }

        _selectedVariableName = (VariablesList.SelectedItem as VariableEntry)?.Name;
        var wasUpdating = _updating;
        _updating = true;
        VariableNameBox.Text = _selectedVariableName ?? string.Empty;
        VariableValueBox.Text = _selectedVariableName is not null &&
                                _sequence.Variables.TryGetValue(_selectedVariableName, out var value)
            ? FormatValue(value)
            : string.Empty;
        _updating = wasUpdating;
    }

    private void VariableNameBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_updating || string.IsNullOrWhiteSpace(_selectedVariableName))
        {
            return;
        }

        var newName = VariableNameBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(newName) ||
            string.Equals(newName, _selectedVariableName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_sequence.Variables.ContainsKey(newName))
        {
            StatusText.Text = $"变量已存在：{newName}";
            RefreshVariablesList();
            return;
        }

        var value = _sequence.Variables[_selectedVariableName];
        _sequence.Variables.Remove(_selectedVariableName);
        _sequence.Variables[newName] = value;
        _selectedVariableName = newName;
        RefreshVariablesList();
    }

    private void VariableValueBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_updating || string.IsNullOrWhiteSpace(_selectedVariableName))
        {
            return;
        }

        _sequence.Variables[_selectedVariableName] = ParseEditorValue(VariableValueBox.Text);
    }

    private void AddVariable_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var name = MakeUniqueName(_sequence.Variables.Keys, "variable");
        _sequence.Variables[name] = string.Empty;
        _selectedVariableName = name;
        RefreshVariablesList();
    }

    private void DeleteVariable_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedVariableName))
        {
            return;
        }

        _sequence.Variables.Remove(_selectedVariableName);
        _selectedVariableName = null;
        RefreshVariablesList();
    }

    private void AddStep_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selectedItem is null || PluginCombo.SelectedItem is not ITestStepPlugin plugin)
        {
            return;
        }

        var step = CreateStep(plugin, plugin.Descriptor.DisplayName);
        GetCurrentSteps()?.Add(step);

        if (_selectedSection == StepSection.Main && string.IsNullOrWhiteSpace(_selectedItem.VerdictSource.StepId))
        {
            _selectedItem.VerdictSource.StepId = step.Id;
        }

        _selectedStep = step;
        RefreshItemPanel();
    }

    private async void EditStep_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await OpenSelectedStepEditorAsync();
    }

    private async void StepsList_OnDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (sender is ListBox listBox && listBox.SelectedItem is TestStepDefinition step)
        {
            _selectedSection = listBox == InitStepsList ? StepSection.Init :
                listBox == MainStepsList ? StepSection.Main :
                StepSection.Cleanup;
            SectionTabs.SelectedIndex = _selectedSection == StepSection.Init ? 0 : _selectedSection == StepSection.Main ? 1 : 2;
            _selectedStep = step;
            SelectStepInList();
        }

        await OpenSelectedStepEditorAsync();
    }

    private async Task OpenSelectedStepEditorAsync()
    {
        if (_selectedStep is null)
        {
            return;
        }

        var editor = new StepEditorWindow(_selectedStep, _pluginRegistry, _settingsEditorRegistry, _sequence.Variables);
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            await editor.ShowDialog(owner);
        }
        else
        {
            editor.Show();
        }

        RefreshStepLists();
        RefreshVerdictControls();
    }

    private void CopyStep_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var steps = GetCurrentSteps();
        if (steps is null || _selectedStep is null)
        {
            return;
        }

        var index = steps.IndexOf(_selectedStep);
        var copy = _selectedStep.Clone();
        steps.Insert(index + 1, copy);
        _selectedStep = copy;
        RefreshItemPanel();
    }

    private void DeleteStep_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var steps = GetCurrentSteps();
        if (steps is null || _selectedStep is null)
        {
            return;
        }

        var index = steps.IndexOf(_selectedStep);
        steps.Remove(_selectedStep);

        if (_selectedItem is not null && _selectedItem.VerdictSource.StepId == _selectedStep.Id)
        {
            _selectedItem.VerdictSource.StepId = _selectedItem.MainSteps.FirstOrDefault()?.Id;
        }

        _selectedStep = steps.ElementAtOrDefault(Math.Clamp(index, 0, Math.Max(0, steps.Count - 1)));
        RefreshItemPanel();
    }

    private void MoveStepUp_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        MoveStep(-1);
    }

    private void MoveStepDown_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        MoveStep(1);
    }

    private void MoveStep(int delta)
    {
        var steps = GetCurrentSteps();
        if (steps is null || _selectedStep is null)
        {
            return;
        }

        var oldIndex = steps.IndexOf(_selectedStep);
        var newIndex = oldIndex + delta;
        if (oldIndex < 0 || newIndex < 0 || newIndex >= steps.Count)
        {
            return;
        }

        steps.RemoveAt(oldIndex);
        steps.Insert(newIndex, _selectedStep);
        RefreshStepLists();
    }

    private void SequenceNameBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_updating)
        {
            return;
        }

        var text = SequenceNameBox.Text ?? string.Empty;
        if (string.Equals(_sequence.Name, text, StringComparison.Ordinal))
        {
            return;
        }

        _sequence.Name = text;
    }

    private void ItemsList_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating)
        {
            return;
        }

        var selected = ItemsList.SelectedItem as TestItemDefinition;
        if (ReferenceEquals(selected, _selectedItem))
        {
            return;
        }

        _selectedItem = selected;
        _selectedStep = null;
        RefreshItemPanel();
    }

    private void ItemNameBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_updating || _selectedItem is null)
        {
            return;
        }

        var text = ItemNameBox.Text ?? string.Empty;
        if (string.Equals(_selectedItem.Name, text, StringComparison.Ordinal))
        {
            return;
        }

        _selectedItem.Name = text;
        RefreshItemsList();
    }

    private void ItemEnabledBox_OnChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_updating || _selectedItem is null)
        {
            return;
        }

        _selectedItem.Enabled = ItemEnabledBox.IsChecked == true;
    }

    private void VerdictStepCombo_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating || _selectedItem is null)
        {
            return;
        }

        var stepId = (VerdictStepCombo.SelectedItem as TestStepDefinition)?.Id;
        if (string.Equals(_selectedItem.VerdictSource.StepId, stepId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _selectedItem.VerdictSource.StepId = stepId;
    }

    private void VerdictOutputBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_updating || _selectedItem is null)
        {
            return;
        }

        var outputKey = string.IsNullOrWhiteSpace(VerdictOutputBox.Text)
            ? null
            : VerdictOutputBox.Text;
        if (string.Equals(_selectedItem.VerdictSource.OutputKey, outputKey, StringComparison.Ordinal))
        {
            return;
        }

        _selectedItem.VerdictSource.OutputKey = outputKey;
    }

    private void SectionTabs_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating)
        {
            return;
        }

        var section = SectionTabs.SelectedIndex switch
        {
            0 => StepSection.Init,
            1 => StepSection.Main,
            2 => StepSection.Cleanup,
            _ => StepSection.Init
        };

        if (section == _selectedSection)
        {
            return;
        }

        _selectedSection = section;
        _selectedStep = GetStepListBox(_selectedSection).SelectedItem as TestStepDefinition;

    }

    private void StepsList_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating || _clearingStepSelection || sender is not ListBox listBox || listBox.SelectedItem is not TestStepDefinition step)
        {
            return;
        }

        var section = listBox == InitStepsList ? StepSection.Init :
            listBox == MainStepsList ? StepSection.Main :
            StepSection.Cleanup;
        if (section == _selectedSection && ReferenceEquals(step, _selectedStep))
        {
            return;
        }

        _selectedSection = section;
        SectionTabs.SelectedIndex = _selectedSection == StepSection.Init ? 0 : _selectedSection == StepSection.Main ? 1 : 2;
        _selectedStep = step;
        SelectStepInList();

    }

    private void RefreshItemsList()
    {
        var wasUpdating = _updating;
        _updating = true;

        var selected = _selectedItem;
        ItemsList.ItemsSource = null;
        ItemsList.ItemsSource = _sequence.Items;
        ItemsList.SelectedItem = selected;

        _updating = wasUpdating;
    }
}
