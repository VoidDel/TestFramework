using System.Globalization;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using TestFramework.Abstractions.Models;
using TestFramework.Abstractions.Plugins;
using TestFramework.App.Services;
using TestFramework.Core.Plugins;
using TestFramework.Core.Resources;
using TestFramework.Plugin.Abstractions.UI;
using TestFramework.Plugins.BasicSteps;
using TestFramework.SequenceYaml;
using TestFramework.SequenceYaml.Validation;

namespace TestFramework.App.Views;

public sealed partial class SequenceEditorView : UserControl
{
    private readonly PluginRegistry _pluginRegistry = new();
    private readonly ResourcePluginRegistry _resourcePluginRegistry = new();
    private readonly PluginSettingsEditorRegistry _settingsEditorRegistry = new();
    private readonly TestSequenceYamlService _yamlService = new();
    private readonly SequenceDocumentStore _documentStore;
    private readonly SequenceRunService _runService;
    private readonly TestResultStore _resultStore;
    private readonly TestSequenceValidator _validator;

    private TestSequence _sequence = new();
    private readonly List<SequenceDocument> _sequenceDocuments = [];
    private SequenceDocument? _currentDocument;
    private TestItemDefinition? _selectedItem;
    private TestStepDefinition? _selectedStep;
    private StepSection _selectedSection = StepSection.Init;
    private string? _selectedVariableName;
    private bool _updating = true;
    private bool _isRunning;
    private bool _numericLowerInputValid = true;
    private bool _numericUpperInputValid = true;
    private readonly Dictionary<ITestStepPlugin, string> _pluginCategories = [];
    private static readonly DataFormat<SequenceTreeDragData> SequenceTreeNodeDataFormat =
        DataFormat.CreateInProcessFormat<SequenceTreeDragData>("application/x-testframework-sequence-tree-node");
    private SequenceTreeNode? _dragSourceNode;
    private PointerPressedEventArgs? _dragStartEvent;
    private Avalonia.Point _dragStartPoint;
    private bool _dragInProgress;
    private CancellationTokenSource? _runCts;
    private bool _isDirty;

    private enum StatusKind
    {
        Info,
        Running,
        Success,
        Warning,
        Error
    }

    public enum TreeNodeKind
    {
        Sequence,
        Item,
        Section,
        Step
    }

    public sealed class SequenceTreeNode
    {
        public required string Title { get; init; }

        public required TreeNodeKind Kind { get; init; }

        public required string Key { get; init; }

        public bool IsExpanded { get; set; }

        public TestItemDefinition? Item { get; init; }

        public StepSection? Section { get; init; }

        public TestStepDefinition? Step { get; init; }

        public List<SequenceTreeNode> Children { get; } = [];

        public string DisplayName { get; init; } = string.Empty;

        public string? BadgeText { get; init; }

        public bool IsDisabled { get; init; }

        public StreamGeometry? IconData { get; init; }
    }

    public sealed class VariableEntry
    {
        public required string Name { get; init; }

        public required string ValueText { get; init; }
    }

    public sealed class PluginTreeNode
    {
        public required string Title { get; init; }

        public ITestStepPlugin? Plugin { get; init; }

        public List<PluginTreeNode> Children { get; } = [];

        public StreamGeometry? IconData => Plugin is not null ? GetIcon("IconPlugin") : GetIcon("IconFolder");
    }

    private sealed class SequenceTreeDragData
    {
        public required SequenceTreeNode Node { get; init; }

        public required bool Copy { get; init; }
    }

    public sealed class Option<T>
    {
        public required T Value { get; init; }

        public required string Text { get; init; }

        public override string ToString() => Text;
    }

    public SequenceEditorView()
        : this(SequenceDirectory, ResultDirectory)
    {
    }

    internal SequenceEditorView(string sequenceDirectory, string resultDirectory)
    {
        InitializeComponent();

        // TreeViewItem handles pointer presses while selecting a node. Listen on the
        // tunnel route so the drag source is captured before that happens.
        SequenceTree.AddHandler(
            InputElement.PointerPressedEvent,
            SequenceTree_OnPointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        SequenceTree.AddHandler(
            InputElement.PointerMovedEvent,
            SequenceTree_OnPointerMoved,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        SequenceTree.AddHandler(
            InputElement.PointerReleasedEvent,
            SequenceTree_OnPointerReleased,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);

        RegisterPlugins();
        RegisterSettingsEditors();
        _documentStore = new SequenceDocumentStore(_yamlService, sequenceDirectory, CreateDefaultSequence);
        _runService = new SequenceRunService(_pluginRegistry, _resourcePluginRegistry);
        _resultStore = new TestResultStore(resultDirectory);
        _validator = new TestSequenceValidator(_pluginRegistry, _resourcePluginRegistry);


        VerdictTypeCombo.ItemsSource = new[]
        {
            new Option<VerdictJudgeType> { Value = VerdictJudgeType.PassFail, Text = "Pass/Fail" },
            new Option<VerdictJudgeType> { Value = VerdictJudgeType.Numeric, Text = "值判定" },
            new Option<VerdictJudgeType> { Value = VerdictJudgeType.String, Text = "字符串判定" }
        };
        StringModeCombo.ItemsSource = new[]
        {
            new Option<StringJudgeMode> { Value = StringJudgeMode.Exact, Text = "完全一致" },
            new Option<StringJudgeMode> { Value = StringJudgeMode.Regex, Text = "正则匹配" }
        };

        LoadSequenceDocuments();
        RefreshAll();
        UpdateWindowTitle();
        SyncThemeToggleIcon();

        // 全局快捷键：Ctrl+S 保存、Ctrl+Shift+S 另存、Ctrl+N 新建、F5 运行/停止。
        AddHandler(InputElement.KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (_isRunning && e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key is Key.S or Key.N)
        {
            e.Handled = true;
            return;
        }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.S)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                SaveAs_OnClick(this, new Avalonia.Interactivity.RoutedEventArgs());
            }
            else
            {
                Save_OnClick(this, new Avalonia.Interactivity.RoutedEventArgs());
            }
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.N)
        {
            New_OnClick(this, new Avalonia.Interactivity.RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F5 && e.KeyModifiers == KeyModifiers.None)
        {
            Run_OnClick(this, new Avalonia.Interactivity.RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void RegisterPlugins()
    {
        RegisterBuiltInPlugin(new DelayStepPlugin());
        RegisterBuiltInPlugin(new LogStepPlugin());
        RegisterBuiltInPlugin(new LimitCheckStepPlugin());
        RegisterBuiltInPlugin(new ThrowStepPlugin());

        var pluginDirectory = Path.Combine(AppContext.BaseDirectory, "Plugins");
        var report = new PluginDirectoryLoader(_pluginRegistry, _resourcePluginRegistry).LoadFromDirectory(pluginDirectory);

        foreach (var plugin in report.StepPlugins)
        {
            _pluginCategories[plugin] = GetPluginCategory(pluginDirectory, report.PluginPaths[plugin]);
        }

        RegisterSettingsEditors(report.StepPlugins);
        foreach (var failure in report.Failures)
        {
            AppendLog($"插件加载失败：{failure.AssemblyPath} - {failure.Message}");
        }
    }

    private void RegisterBuiltInPlugin(ITestStepPlugin plugin)
    {
        _pluginRegistry.Register(plugin);
        _pluginCategories[plugin] = "内置";
    }

    private static string GetPluginCategory(string pluginDirectory, string assemblyPath)
    {
        var relativeDirectory = Path.GetRelativePath(pluginDirectory, Path.GetDirectoryName(assemblyPath) ?? pluginDirectory);
        if (string.IsNullOrWhiteSpace(relativeDirectory) || relativeDirectory == ".")
        {
            return "外部";
        }

        return relativeDirectory.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private IReadOnlyList<PluginTreeNode> BuildPluginTree()
    {
        var roots = new SortedDictionary<string, PluginTreeNode>(StringComparer.Ordinal);
        var categories = new Dictionary<string, PluginTreeNode>(StringComparer.Ordinal);
        foreach (var plugin in _pluginRegistry.Plugins.OrderBy(plugin => plugin.Descriptor.DisplayName, StringComparer.CurrentCulture))
        {
            var category = _pluginCategories.TryGetValue(plugin, out var value) ? value : "外部";
            var parent = EnsurePluginCategory(roots, categories, category);
            parent.Children.Add(new PluginTreeNode
            {
                Title = plugin.Descriptor.DisplayName,
                Plugin = plugin
            });
        }

        return roots.Values.ToList();
    }

    private static PluginTreeNode EnsurePluginCategory(
        IDictionary<string, PluginTreeNode> roots,
        IDictionary<string, PluginTreeNode> categories,
        string category)
    {
        PluginTreeNode? parent = null;
        var categoryKey = string.Empty;
        foreach (var segment in category.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            categoryKey = string.IsNullOrEmpty(categoryKey) ? segment : $"{categoryKey}/{segment}";
            if (!categories.TryGetValue(categoryKey, out var node))
            {
                node = new PluginTreeNode { Title = segment };
                categories.Add(categoryKey, node);
                if (parent is null)
                {
                    roots.Add(segment, node);
                }
                else
                {
                    parent.Children.Add(node);
                }
            }

            parent = node;
        }

        return parent ?? throw new InvalidOperationException("插件分类不能为空。");
    }

    private static IEnumerable<PluginTreeNode> FlattenPluginTree(PluginTreeNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var descendant in FlattenPluginTree(child))
            {
                yield return descendant;
            }
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
                OutputKey = "verdict",
                JudgeType = VerdictJudgeType.PassFail
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

    private static string SequenceDirectory =>
        Path.Combine(AppContext.BaseDirectory, "config", "sequence");

    private static string ResultDirectory =>
        Path.Combine(AppContext.BaseDirectory, "results");

    private void LoadSequenceDocuments()
    {
        var loadResult = _documentStore.Load();

        _sequenceDocuments.Clear();
        _sequenceDocuments.AddRange(loadResult.Documents);

        foreach (var failure in loadResult.Failures)
        {
            AppendLog($"序列加载失败：{failure.FilePath} - {failure.Message}");
        }

        SetCurrentDocument(loadResult.CurrentDocument);
    }

    private SequenceDocument CreateNewSequenceDocument()
    {
        return _documentStore.CreateNew();
    }

    private void SetCurrentDocument(SequenceDocument document)
    {
        _currentDocument = document;
        _sequence = document.Sequence;
        _selectedItem = _sequence.Items.FirstOrDefault();
        _selectedStep = null;
        _selectedVariableName = null;
        _isDirty = document.IsDirty;
        UpdateWindowTitle();
    }

    private void RefreshAll()
    {
        _updating = true;
        RefreshSequenceCombo();
        SequenceNameBox.Text = _sequence.Name;
        _selectedItem ??= _sequence.Items.FirstOrDefault();
        RefreshSequenceTree();
        RefreshVariablesList();
        RefreshItemPanel();
        _updating = false;
    }

    private void RefreshSequenceCombo()
    {
        SequenceCombo.ItemsSource = null;
        SequenceCombo.ItemsSource = _sequenceDocuments;
        SequenceCombo.SelectedItem = _currentDocument;
    }

    private void RefreshItemPanel()
    {
        _updating = true;

        ItemNameBox.Text = _selectedItem?.Name ?? string.Empty;
        ItemEnabledBox.IsChecked = _selectedItem?.Enabled ?? false;

        RefreshVerdictControls();
        RefreshTreeSelectionText();

        _updating = false;
    }

    private void RefreshSequenceTree()
    {
        var wasUpdating = _updating;
        _updating = true;

        var expandedNodes = GetExpandedNodeKeys(SequenceTree.ItemsSource);
        var root = BuildSequenceTree();
        SequenceTree.ItemsSource = new[] { root };
        RestoreExpandedNodes(root, expandedNodes);
        SequenceTree.SelectedItem = FindCurrentTreeNode(root) ?? root;

        _updating = wasUpdating;
    }

    private static HashSet<string> GetExpandedNodeKeys(object? itemsSource)
    {
        var expandedNodes = new HashSet<string>(StringComparer.Ordinal);
        if (itemsSource is not IEnumerable<SequenceTreeNode> roots)
        {
            return expandedNodes;
        }

        foreach (var node in roots)
        {
            CollectExpandedNodeKeys(node, expandedNodes);
        }

        return expandedNodes;
    }

    private static void CollectExpandedNodeKeys(SequenceTreeNode node, ISet<string> expandedNodes)
    {
        if (node.IsExpanded)
        {
            expandedNodes.Add(node.Key);
        }

        foreach (var child in node.Children)
        {
            CollectExpandedNodeKeys(child, expandedNodes);
        }
    }

    private static void RestoreExpandedNodes(SequenceTreeNode node, ISet<string> expandedNodes)
    {
        node.IsExpanded = expandedNodes.Contains(node.Key);
        foreach (var child in node.Children)
        {
            RestoreExpandedNodes(child, expandedNodes);
        }
    }

    private void RefreshVerdictControls()
    {
        var wasUpdating = _updating;
        _updating = true;
        _numericLowerInputValid = true;
        _numericUpperInputValid = true;
        NumericLowerBox.SetValue(DataValidationErrors.ErrorsProperty, null);
        NumericUpperBox.SetValue(DataValidationErrors.ErrorsProperty, null);

        VerdictStepCombo.ItemsSource = null;
        VerdictStepCombo.ItemsSource = _selectedItem?.MainSteps;

        if (_selectedItem is null)
        {
            VerdictStepCombo.SelectedItem = null;
            VerdictOutputBox.Text = string.Empty;
            VerdictTypeCombo.SelectedItem = null;
            NumericLowerBox.Text = string.Empty;
            NumericUpperBox.Text = string.Empty;
            NumericSourceUnitBox.Text = string.Empty;
            NumericUnitBox.Text = string.Empty;
            StringModeCombo.SelectedItem = null;
            ExpectedStringBox.Text = string.Empty;
            NumericJudgePanel.IsVisible = false;
            StringJudgePanel.IsVisible = false;
            _updating = wasUpdating;
            return;
        }

        VerdictStepCombo.SelectedItem = _selectedItem.MainSteps.FirstOrDefault(step =>
            string.Equals(step.Id, _selectedItem.VerdictSource.StepId, StringComparison.OrdinalIgnoreCase));
        VerdictOutputBox.Text = _selectedItem.VerdictSource.OutputKey ?? string.Empty;
        SelectOption(VerdictTypeCombo, _selectedItem.VerdictSource.JudgeType);
        NumericLowerBox.Text = EditorValueConverter.FormatNullableDouble(_selectedItem.VerdictSource.LowerLimit);
        NumericUpperBox.Text = EditorValueConverter.FormatNullableDouble(_selectedItem.VerdictSource.UpperLimit);
        NumericSourceUnitBox.Text = _selectedItem.VerdictSource.SourceUnit ?? string.Empty;
        NumericUnitBox.Text = _selectedItem.VerdictSource.Unit ?? string.Empty;
        SelectOption(StringModeCombo, _selectedItem.VerdictSource.StringMode);
        ExpectedStringBox.Text = _selectedItem.VerdictSource.ExpectedString ?? string.Empty;
        NumericJudgePanel.IsVisible = _selectedItem.VerdictSource.JudgeType == VerdictJudgeType.Numeric;
        StringJudgePanel.IsVisible = _selectedItem.VerdictSource.JudgeType == VerdictJudgeType.String;
        _updating = wasUpdating;
    }

    private static StreamGeometry? GetIcon(string resourceKey)
    {
        return Application.Current?.TryFindResource(resourceKey, out var res) == true && res is StreamGeometry geom
            ? geom
            : null;
    }

    private SequenceTreeNode BuildSequenceTree()
    {
        var root = new SequenceTreeNode
        {
            Title = _sequence.Name,
            DisplayName = _sequence.Name,
            BadgeText = $"{_sequence.Items.Count} 项",
            Kind = TreeNodeKind.Sequence,
            Key = "sequence",
            IsExpanded = true,
            IconData = GetIcon("IconSequence")
        };

        foreach (var item in _sequence.Items)
        {
            var itemNode = new SequenceTreeNode
            {
                Title = item.Enabled ? item.Name : $"{item.Name}（禁用）",
                DisplayName = item.Name,
                BadgeText = item.Enabled ? null : "禁用",
                IsDisabled = !item.Enabled,
                Kind = TreeNodeKind.Item,
                Key = $"item:{item.Id}",
                IsExpanded = true,
                Item = item,
                IconData = GetIcon("IconItem")
            };
            itemNode.Children.Add(BuildSectionNode(item, StepSection.Init, "初始化", item.InitSteps));
            itemNode.Children.Add(BuildSectionNode(item, StepSection.Main, "主流程", item.MainSteps));
            itemNode.Children.Add(BuildSectionNode(item, StepSection.Cleanup, "清理", item.CleanupSteps));
            root.Children.Add(itemNode);
        }

        return root;
    }

    private static SequenceTreeNode BuildSectionNode(
        TestItemDefinition item,
        StepSection section,
        string title,
        IReadOnlyList<TestStepDefinition> steps)
    {
        var sectionIconKey = section switch
        {
            StepSection.Init => "IconInit",
            StepSection.Main => "IconMain",
            StepSection.Cleanup => "IconCleanup",
            _ => "IconFolder"
        };

        var sectionNode = new SequenceTreeNode
        {
            Title = $"{title} ({steps.Count})",
            DisplayName = title,
            BadgeText = $"{steps.Count}",
            Kind = TreeNodeKind.Section,
            Key = $"item:{item.Id}:section:{section}",
            IsExpanded = true,
            Item = item,
            Section = section,
            IconData = GetIcon(sectionIconKey)
        };

        foreach (var step in steps)
        {
            sectionNode.Children.Add(new SequenceTreeNode
            {
                Title = step.Enabled ? step.Name : $"{step.Name}（禁用）",
                DisplayName = step.Name,
                BadgeText = step.Enabled ? null : "禁用",
                IsDisabled = !step.Enabled,
                Kind = TreeNodeKind.Step,
                Key = $"item:{item.Id}:section:{section}:step:{step.Id}",
                Item = item,
                Section = section,
                Step = step,
                IconData = GetIcon("IconStep")
            });
        }

        return sectionNode;
    }

    private SequenceTreeNode? FindCurrentTreeNode(SequenceTreeNode root)
    {
        return FlattenTree(root).FirstOrDefault(node =>
            _selectedStep is not null &&
            node.Step is not null &&
            string.Equals(node.Step.Id, _selectedStep.Id, StringComparison.OrdinalIgnoreCase)) ??
               FlattenTree(root).FirstOrDefault(node =>
                   _selectedItem is not null &&
                   node.Kind == TreeNodeKind.Section &&
                   ReferenceEquals(node.Item, _selectedItem) &&
                   node.Section == _selectedSection) ??
               FlattenTree(root).FirstOrDefault(node =>
                   _selectedItem is not null &&
                   node.Kind == TreeNodeKind.Item &&
                   ReferenceEquals(node.Item, _selectedItem));
    }

    private static IEnumerable<SequenceTreeNode> FlattenTree(SequenceTreeNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var descendant in FlattenTree(child))
            {
                yield return descendant;
            }
        }
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

    private void RefreshTreeSelectionText()
    {
        TreeSelectionText.Text = _selectedItem is null
            ? "在左侧树中选择测试项或 Step。"
            : _selectedStep is null
                ? $"当前测试项：{_selectedItem.Name}，添加 Step 将进入“{DisplayFormatters.FormatSection(_selectedSection)}”。"
                : $"当前 Step：{_selectedStep.Name}（{DisplayFormatters.FormatSection(_selectedSection)}）。";
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
                ValueText = EditorValueConverter.Format(pair.Value)
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

    private static string? EmptyToNull(string? text)
    {
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static void SelectOption<T>(ComboBox comboBox, T value)
    {
        comboBox.SelectedItem = comboBox.ItemsSource?
            .OfType<Option<T>>()
            .FirstOrDefault(option => EqualityComparer<T>.Default.Equals(option.Value, value));
    }

    private static T? SelectedOptionValue<T>(ComboBox comboBox)
    {
        return comboBox.SelectedItem is Option<T> option ? option.Value : default;
    }

    /// <summary>Upper bound on retained log characters; see <see cref="AppendLog"/>.</summary>
    private const int MaxLogLength = 256 * 1024;

    private readonly System.Text.StringBuilder _logBuffer = new();

    /// <summary>
    /// Appends one log line. The text is accumulated in a buffer and trimmed to
    /// <see cref="MaxLogLength"/>: reading and re-assigning <c>LogBox.Text</c> per line is
    /// quadratic in the log size and grows without bound over a long run, which starves the UI
    /// thread exactly when a long sequence is producing the most events.
    /// </summary>
    private void AppendLog(string message)
    {
        _logBuffer.Append(CultureInfo.CurrentCulture, $"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}");
        TrimLogBuffer();
        LogBox.Text = _logBuffer.ToString();
        LogBox.CaretIndex = _logBuffer.Length;
    }

    private void TrimLogBuffer()
    {
        if (_logBuffer.Length <= MaxLogLength)
        {
            return;
        }

        // Drop whole lines from the front so the remaining text stays readable.
        var cut = _logBuffer.Length - MaxLogLength;
        while (cut < _logBuffer.Length && _logBuffer[cut] != '\n')
        {
            cut++;
        }

        _logBuffer.Remove(0, Math.Min(cut + 1, _logBuffer.Length));
    }

    private void ResetLog()
    {
        _logBuffer.Clear();
        LogBox.Text = string.Empty;
    }

    private void ClearLog_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ResetLog();
    }

    private async void CopyLog_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard && !string.IsNullOrEmpty(LogBox.Text))
            {
                await clipboard.SetTextAsync(LogBox.Text);
                SetStatus("已复制日志到剪贴板。");
            }
        }
        catch (Exception ex)
        {
            ReportOperationFailure("复制日志失败", ex);
        }
    }

    private bool ValidateForSaveOrRun()
    {
        if (_selectedItem?.VerdictSource.JudgeType == VerdictJudgeType.Numeric &&
            (!_numericLowerInputValid || !_numericUpperInputValid))
        {
            SetStatus("数值限值格式无效。", StatusKind.Error);
            AppendLog("校验失败：数值限值必须使用小数点格式，例如 4.8。");
            return false;
        }

        var issues = _validator.Validate(_sequence);
        var errors = issues.Where(issue => issue.IsError).ToArray();
        var warnings = issues.Where(issue => !issue.IsError).ToArray();

        // Warnings are reported but never block. A plugin version that will be substituted is worth
        // telling the operator about; refusing to run over it would be worse than the substitution.
        foreach (var warning in warnings)
        {
            AppendLog("  警告：" + warning.Message);
        }

        if (errors.Length == 0)
        {
            SetStatus(
                warnings.Length == 0 ? "校验通过。" : $"校验通过，{warnings.Length} 个警告。",
                warnings.Length == 0 ? StatusKind.Success : StatusKind.Warning);
            return true;
        }

        SetStatus($"校验失败：{errors.Length} 个问题。", StatusKind.Error);
        AppendLog("校验失败：");
        foreach (var issue in errors)
        {
            AppendLog("  " + issue.Message);
        }

        return false;
    }

    private SequenceDocument EnsureCurrentDocument()
    {
        _currentDocument ??= CreateNewSequenceDocument();
        if (!_sequenceDocuments.Contains(_currentDocument))
        {
            _sequenceDocuments.Add(_currentDocument);
        }

        return _currentDocument;
    }

    private void New_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var document = CreateNewSequenceDocument();
        _sequenceDocuments.Add(document);
        SetCurrentDocument(document);
        MarkDirty();
        RefreshAll();
        AppendLog("已新建测试序列。");
    }

    private async void Save_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!ValidateForSaveOrRun())
        {
            return;
        }

        try
        {
            var file = await SaveDocumentAsync(EnsureCurrentDocument());
            RefreshSequenceCombo();
            SetStatus("保存成功。", StatusKind.Success);
            AppendLog($"已保存：{file}");
        }
        catch (Exception ex)
        {
            ReportOperationFailure("保存失败", ex);
        }
    }

    private async void SaveAs_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            await SaveAsAsync();
        }
        catch (Exception ex)
        {
            ReportOperationFailure("另存失败", ex);
        }
    }

    private async Task SaveAsAsync()
    {
        if (!ValidateForSaveOrRun())
        {
            return;
        }

        var document = await _documentStore.SaveCopyAsync(EnsureCurrentDocument(), _sequenceDocuments);
        _sequenceDocuments.Add(document);
        SetCurrentDocument(document);
        RefreshAll();
        AppendLog($"已保存：{document.FilePath}");
    }

    private async void DeleteSequence_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_currentDocument is null)
        {
            return;
        }

        try
        {
            var removed = _currentDocument;

            // Deleting removes the file outright - it does not go to the recycle bin - so it is
            // confirmed like the unsaved-changes prompt rather than acting on a single click.
            if (!await ConfirmDeleteSequenceAsync(removed))
            {
                return;
            }

            if (_documentStore.DeleteFile(removed))
            {
                AppendLog($"已删除测试序列文件：{removed.FilePath}");
            }

            _sequenceDocuments.Remove(removed);
            if (_sequenceDocuments.Count == 0)
            {
                _sequenceDocuments.Add(CreateNewSequenceDocument());
            }

            SetCurrentDocument(_sequenceDocuments[0]);
            RefreshAll();
        }
        catch (Exception ex)
        {
            ReportOperationFailure("删除失败", ex);
        }
    }

    private async void Run_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_isRunning)
        {
            // 运行中点击 = 停止
            _runCts?.Cancel();
            return;
        }

        if (!ValidateForSaveOrRun())
        {
            return;
        }

        ResetLog();
        var runCancellation = new CancellationTokenSource();
        _runCts = runCancellation;
        SetRunningState(true);
        try
        {
            var snapshot = _yamlService.Load(_yamlService.Save(_sequence));
            var result = await _runService.RunAsync(snapshot, new UiExecutionObserver(AppendLog), runCancellation.Token);
            var resultFile = await _resultStore.SaveAsync(result);
            SetStatus(result.Verdict == TestVerdict.Cancelled ? "运行已取消，部分结果已保存。" : $"运行完成：{DisplayFormatters.FormatVerdict(result.Verdict)}",
                result.Verdict == TestVerdict.Pass ? StatusKind.Success : result.Verdict == TestVerdict.Cancelled ? StatusKind.Info : StatusKind.Error);
            AppendLog($"结果已保存：{resultFile}");
            foreach (var error in result.ResourceErrors) AppendLog(error);
            if (result.HasPendingExecution)
            {
                AppendLog("插件未响应停止，已中止序列。后台步骤退出并完成资源清理前不能再次运行。");
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus("运行已取消。", StatusKind.Info);
            AppendLog("运行已取消。");
        }
        catch (Exception ex)
        {
            SetStatus("运行失败。", StatusKind.Error);
            AppendLog("运行失败：" + ex.Message);
        }
        finally
        {
            SetRunningState(false);
            _runCts = null;
            _ = DisposeRunCancellationAsync(runCancellation, _runService.PendingRecovery);
        }
    }

    private static async Task DisposeRunCancellationAsync(CancellationTokenSource cancellation, Task recovery)
    {
        try { await recovery.ConfigureAwait(false); }
        finally { cancellation.Dispose(); }
    }

    private void AddItem_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var item = new TestItemDefinition { Name = "新测试项" };
        var plugin = _pluginRegistry.GetRequired("basic.limit-check");
        var main = CreateStep(plugin, "主检查");
        item.MainSteps.Add(main);
        item.VerdictSource.StepId = main.Id;
        item.VerdictSource.OutputKey = "verdict";
        item.VerdictSource.JudgeType = VerdictJudgeType.PassFail;

        _sequence.Items.Add(item);
        MarkDirty();
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
        MarkDirty();
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
            ? EditorValueConverter.Format(value)
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
            SetStatus($"变量已存在：{newName}", StatusKind.Error);
            RefreshVariablesList();
            return;
        }

        var value = _sequence.Variables[_selectedVariableName];
        _sequence.Variables.Remove(_selectedVariableName);
        _sequence.Variables[newName] = value;
        _selectedVariableName = newName;
        MarkDirty();
        RefreshVariablesList();
    }

    private void VariableValueBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_updating || string.IsNullOrWhiteSpace(_selectedVariableName))
        {
            return;
        }

        var parsed = EditorValueConverter.Parse(VariableValueBox.Text);
        if (!Equals(_sequence.Variables[_selectedVariableName], parsed))
        {
            _sequence.Variables[_selectedVariableName] = parsed;
            MarkDirty();
        }
    }

    private void AddVariable_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var name = UniqueNameGenerator.Create(_sequence.Variables.Keys, "variable");
        _sequence.Variables[name] = string.Empty;
        _selectedVariableName = name;
        MarkDirty();
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
        MarkDirty();
        RefreshVariablesList();
    }

    private async void AddStep_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            await AddStepAsync();
        }
        catch (Exception ex)
        {
            ReportOperationFailure("添加步骤失败", ex);
        }
    }

    private async Task AddStepAsync()
    {
        if (SequenceTree.SelectedItem is SequenceTreeNode node)
        {
            ApplyTreeSelection(node);
        }

        if (_selectedItem is null)
        {
            return;
        }

        var picker = new StepPluginPickerWindow(BuildPluginTree());
        ITestStepPlugin? plugin;
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            plugin = await picker.ShowDialog<ITestStepPlugin?>(owner);
        }
        else
        {
            picker.Show();
            return;
        }

        if (plugin is null)
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
        MarkDirty();
        RefreshSequenceTree();
        RefreshItemPanel();
    }

    private async void EditStep_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            if (SequenceTree.SelectedItem is SequenceTreeNode node)
            {
                ApplyTreeSelection(node);
            }

            await OpenSelectedStepEditorAsync();
        }
        catch (Exception ex)
        {
            ReportOperationFailure("打开步骤编辑器失败", ex);
        }
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
            // 编辑器直接修改共享模型，关闭即视为可能已修改
            MarkDirty();
        }
        else
        {
            editor.Show();
        }

        RefreshSequenceTree();
        RefreshVerdictControls();
        RefreshTreeSelectionText();
    }

    private void CopyStep_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (SequenceTree.SelectedItem is SequenceTreeNode node)
        {
            ApplyTreeSelection(node);
        }

        var steps = GetCurrentSteps();
        if (steps is null || _selectedStep is null)
        {
            return;
        }

        var index = steps.IndexOf(_selectedStep);
        TestStepDefinition copy;
        try
        {
            copy = _selectedStep.Clone();
        }
        catch (Exception ex)
        {
            ReportOperationFailure("复制步骤失败", ex);
            return;
        }

        steps.Insert(index + 1, copy);
        _selectedStep = copy;
        MarkDirty();
        RefreshSequenceTree();
        RefreshItemPanel();
    }

    private void DeleteStep_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        DeleteSelectedTreeNode();
    }

    private void DeleteTreeNode_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        DeleteSelectedTreeNode();
    }

    private void DeleteSelectedTreeNode()
    {
        if (SequenceTree.SelectedItem is not SequenceTreeNode node)
        {
            return;
        }

        ApplyTreeSelection(node);
        if (node.Kind == TreeNodeKind.Item)
        {
            DeleteItem_OnClick(null, null!);
        }
        else if (node.Kind == TreeNodeKind.Step)
        {
            var steps = GetCurrentSteps();
            if (steps is not null && _selectedStep is not null)
            {
                var index = steps.IndexOf(_selectedStep);
                steps.Remove(_selectedStep);
                if (_selectedItem?.VerdictSource.StepId == _selectedStep.Id)
                {
                    _selectedItem.VerdictSource.StepId = _selectedItem.MainSteps.FirstOrDefault()?.Id;
                }

                _selectedStep = steps.ElementAtOrDefault(Math.Clamp(index, 0, Math.Max(0, steps.Count - 1)));
                MarkDirty();
                RefreshSequenceTree();
                RefreshItemPanel();
            }
        }
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
        MarkDirty();
        RefreshSequenceTree();
        RefreshTreeSelectionText();
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
        MarkDirty();
        RefreshSequenceCombo();
        RefreshSequenceTree();
    }

    private void SequenceCombo_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating ||
            SequenceCombo.SelectedItem is not SequenceDocument selected ||
            ReferenceEquals(selected, _currentDocument))
        {
            return;
        }

        SetCurrentDocument(selected);
        RefreshAll();
    }

    private void SequenceTree_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating)
        {
            return;
        }

        if (SequenceTree.SelectedItem is not SequenceTreeNode node)
        {
            return;
        }

        ApplyTreeSelection(node);
        RefreshItemPanel();
    }

    private async void SequenceTree_OnDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        try
        {
            if (SequenceTree.SelectedItem is SequenceTreeNode { Kind: TreeNodeKind.Step } node)
            {
                ApplyTreeSelection(node);
                await OpenSelectedStepEditorAsync();
            }
        }
        catch (Exception ex)
        {
            ReportOperationFailure("打开步骤编辑器失败", ex);
        }
    }

    private void SequenceTree_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            DeleteSelectedTreeNode();
            e.Handled = true;
        }
    }

    private void SequenceTree_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragSourceNode = FindSequenceTreeNode(e.Source as Visual);
        _dragStartEvent = _dragSourceNode is { Kind: TreeNodeKind.Item or TreeNodeKind.Step } ? e : null;
        _dragStartPoint = e.GetPosition(SequenceTree);
    }

    private async void SequenceTree_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragInProgress || _dragStartEvent is null || _dragSourceNode is null ||
            !e.GetCurrentPoint(SequenceTree).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var currentPoint = e.GetPosition(SequenceTree);
        if (Math.Abs(currentPoint.X - _dragStartPoint.X) < 4 && Math.Abs(currentPoint.Y - _dragStartPoint.Y) < 4)
        {
            return;
        }

        _dragInProgress = true;
        try
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(SequenceTreeNodeDataFormat, new SequenceTreeDragData
            {
                Node = _dragSourceNode,
                Copy = e.KeyModifiers.HasFlag(KeyModifiers.Control)
            }));
            await DragDrop.DoDragDropAsync(
                _dragStartEvent,
                data,
                DragDropEffects.Move | DragDropEffects.Copy);
        }
        catch (Exception ex)
        {
            ReportOperationFailure("拖拽失败", ex);
        }
        finally
        {
            _dragInProgress = false;
            _dragStartEvent = null;
            _dragSourceNode = null;
        }
    }

    private void SequenceTree_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragStartEvent = null;
        _dragSourceNode = null;
    }

    private void SequenceTree_OnDragOver(object? sender, DragEventArgs e)
    {
        var source = e.DataTransfer.TryGetValue(SequenceTreeNodeDataFormat);
        var target = FindSequenceTreeNode(e.Source as Visual);
        if (source is null || target is null || !CanDrop(source.Node, target))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = source.Copy ? DragDropEffects.Copy : DragDropEffects.Move;
    }

    private void SequenceTree_OnDrop(object? sender, DragEventArgs e)
    {
        var source = e.DataTransfer.TryGetValue(SequenceTreeNodeDataFormat);
        var target = FindSequenceTreeNode(e.Source as Visual);
        if (source is null || target is null || !CanDrop(source.Node, target))
        {
            return;
        }

        // A Ctrl+drag copy clones the step, and cloning rejects parameter values it cannot copy
        // safely - a plugin is free to put such a value in Parameters, so this must not escape the
        // handler and take the process down.
        try
        {
            if (source.Node.Kind == TreeNodeKind.Item)
            {
                MoveOrCopyItem(source, target);
            }
            else
            {
                MoveOrCopyStep(source, target);
            }
        }
        catch (Exception ex)
        {
            ReportOperationFailure("拖拽失败", ex);
        }
    }

    private static SequenceTreeNode? FindSequenceTreeNode(Visual? source)
    {
        return source?.GetSelfAndVisualAncestors()
            .OfType<Control>()
            .Select(control => control.DataContext)
            .OfType<SequenceTreeNode>()
            .FirstOrDefault();
    }

    private static bool CanDrop(SequenceTreeNode source, SequenceTreeNode target)
    {
        return source.Kind switch
        {
            TreeNodeKind.Item => target.Kind is TreeNodeKind.Item or TreeNodeKind.Sequence,
            TreeNodeKind.Step => target.Kind is TreeNodeKind.Step or TreeNodeKind.Section,
            _ => false
        };
    }

    private void MoveOrCopyItem(SequenceTreeDragData source, SequenceTreeNode target)
    {
        if (source.Node.Item is null)
        {
            return;
        }

        var item = source.Copy ? CloneItem(source.Node.Item) : source.Node.Item;
        var sourceIndex = _sequence.Items.IndexOf(source.Node.Item);
        var insertIndex = target.Kind == TreeNodeKind.Item && target.Item is not null
            ? _sequence.Items.IndexOf(target.Item)
            : _sequence.Items.Count;

        if (!source.Copy)
        {
            _sequence.Items.Remove(source.Node.Item);
            if (sourceIndex < insertIndex)
            {
                insertIndex--;
            }
        }

        _sequence.Items.Insert(Math.Max(0, insertIndex), item);
        _selectedItem = item;
        _selectedStep = null;
        MarkDirty();
        RefreshAll();
    }

    private void MoveOrCopyStep(SequenceTreeDragData source, SequenceTreeNode target)
    {
        if (source.Node.Item is null || source.Node.Step is null || source.Node.Section is null)
        {
            return;
        }

        var sourceSteps = GetSteps(source.Node.Item, source.Node.Section.Value);
        var targetItem = target.Item;
        var targetSection = target.Kind == TreeNodeKind.Step ? target.Section : target.Section;
        if (sourceSteps is null || targetItem is null || targetSection is null)
        {
            return;
        }

        var targetSteps = GetSteps(targetItem, targetSection.Value);
        if (targetSteps is null)
        {
            return;
        }

        var step = source.Copy ? source.Node.Step.Clone() : source.Node.Step;
        var sourceIndex = sourceSteps.IndexOf(source.Node.Step);
        var insertIndex = target.Kind == TreeNodeKind.Step && target.Step is not null
            ? targetSteps.IndexOf(target.Step)
            : targetSteps.Count;

        if (!source.Copy)
        {
            sourceSteps.Remove(source.Node.Step);
            if (ReferenceEquals(sourceSteps, targetSteps) && sourceIndex < insertIndex)
            {
                insertIndex--;
            }
        }

        targetSteps.Insert(Math.Max(0, insertIndex), step);
        _selectedItem = targetItem;
        _selectedSection = targetSection.Value;
        _selectedStep = step;
        MarkDirty();
        RefreshSequenceTree();
        RefreshItemPanel();
    }

    private static List<TestStepDefinition>? GetSteps(TestItemDefinition item, StepSection section)
    {
        return section switch
        {
            StepSection.Init => item.InitSteps,
            StepSection.Main => item.MainSteps,
            StepSection.Cleanup => item.CleanupSteps,
            _ => null
        };
    }

    private static TestItemDefinition CloneItem(TestItemDefinition item)
    {
        var clone = new TestItemDefinition
        {
            Name = item.Name + " Copy",
            Enabled = item.Enabled,
            VerdictSource = new VerdictSource
            {
                OutputKey = item.VerdictSource.OutputKey,
                JudgeType = item.VerdictSource.JudgeType,
                LowerLimit = item.VerdictSource.LowerLimit,
                UpperLimit = item.VerdictSource.UpperLimit,
                SourceUnit = item.VerdictSource.SourceUnit,
                Unit = item.VerdictSource.Unit,
                StringMode = item.VerdictSource.StringMode,
                ExpectedString = item.VerdictSource.ExpectedString
            },
            InitSteps = item.InitSteps.Select(step => step.Clone()).ToList(),
            MainSteps = item.MainSteps.Select(step => step.Clone()).ToList(),
            CleanupSteps = item.CleanupSteps.Select(step => step.Clone()).ToList()
        };

        clone.VerdictSource.StepId = GetClonedVerdictStepId(item, clone);
        return clone;
    }

    private static string? GetClonedVerdictStepId(TestItemDefinition source, TestItemDefinition clone)
    {
        if (string.IsNullOrWhiteSpace(source.VerdictSource.StepId))
        {
            return null;
        }

        var sourceSteps = source.InitSteps.Concat(source.MainSteps).Concat(source.CleanupSteps).ToList();
        var cloneSteps = clone.InitSteps.Concat(clone.MainSteps).Concat(clone.CleanupSteps).ToList();
        var index = sourceSteps.FindIndex(step => step.Id == source.VerdictSource.StepId);
        return index >= 0 ? cloneSteps[index].Id : null;
    }

    private void ApplyTreeSelection(SequenceTreeNode node)
    {
        switch (node.Kind)
        {
            case TreeNodeKind.Sequence:
                _selectedItem = null;
                _selectedStep = null;
                _selectedSection = StepSection.Main;
                break;
            case TreeNodeKind.Item:
                _selectedItem = node.Item;
                _selectedStep = null;
                _selectedSection = StepSection.Main;
                break;
            case TreeNodeKind.Section:
                _selectedItem = node.Item;
                _selectedStep = null;
                _selectedSection = node.Section ?? StepSection.Main;
                break;
            case TreeNodeKind.Step:
                _selectedItem = node.Item;
                _selectedStep = node.Step;
                _selectedSection = node.Section ?? StepSection.Main;
                break;
        }
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
        MarkDirty();
        RefreshSequenceTree();
    }

    private void ItemEnabledBox_OnChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_updating || _selectedItem is null)
        {
            return;
        }

        var enabled = ItemEnabledBox.IsChecked == true;
        if (_selectedItem.Enabled == enabled)
        {
            return;
        }

        _selectedItem.Enabled = enabled;
        MarkDirty();
        RefreshSequenceTree();
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
        MarkDirty();
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
        MarkDirty();
    }

    private void VerdictTypeCombo_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating || _selectedItem is null)
        {
            return;
        }

        var type = SelectedOptionValue<VerdictJudgeType>(VerdictTypeCombo);
        if (_selectedItem.VerdictSource.JudgeType == type)
        {
            return;
        }

        _selectedItem.VerdictSource.JudgeType = type;
        MarkDirty();
        NumericJudgePanel.IsVisible = type == VerdictJudgeType.Numeric;
        StringJudgePanel.IsVisible = type == VerdictJudgeType.String;
    }

    private void NumericLowerBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_updating && _selectedItem is not null)
        {
            if (EditorValueConverter.TryParseNullableDouble(NumericLowerBox.Text, out var value))
            {
                _numericLowerInputValid = true;
                // TextBox 的 TextChanged 可能经 Dispatcher 延迟触发（初始化赋值后、_updating 已复位），
                // 值未变化时不能标脏。
                if (!Nullable.Equals(_selectedItem.VerdictSource.LowerLimit, value))
                {
                    _selectedItem.VerdictSource.LowerLimit = value;
                    MarkDirty();
                }
                NumericLowerBox.SetValue(DataValidationErrors.ErrorsProperty, null);
            }
            else
            {
                _numericLowerInputValid = false;
                DataValidationErrors.SetErrors(NumericLowerBox, [new InvalidDataException("请输入使用小数点的有效数字。")]);
            }
        }
    }

    private void NumericUpperBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_updating && _selectedItem is not null)
        {
            if (EditorValueConverter.TryParseNullableDouble(NumericUpperBox.Text, out var value))
            {
                _numericUpperInputValid = true;
                if (!Nullable.Equals(_selectedItem.VerdictSource.UpperLimit, value))
                {
                    _selectedItem.VerdictSource.UpperLimit = value;
                    MarkDirty();
                }
                NumericUpperBox.SetValue(DataValidationErrors.ErrorsProperty, null);
            }
            else
            {
                _numericUpperInputValid = false;
                DataValidationErrors.SetErrors(NumericUpperBox, [new InvalidDataException("请输入使用小数点的有效数字。")]);
            }
        }
    }

    private void SetRunningState(bool isRunning)
    {
        _isRunning = isRunning;
        NewButton.IsEnabled = !isRunning;
        SaveButton.IsEnabled = !isRunning;
        SaveAsButton.IsEnabled = !isRunning;
        DeleteSequenceButton.IsEnabled = !isRunning;
        SequenceCombo.IsEnabled = !isRunning;
        SequenceEditingPanel.IsEnabled = !isRunning;

        // 运行中：运行按钮切换为“停止”，状态点启动脉冲动画
        RunButton.Classes.Set("success", !isRunning);
        RunButton.Classes.Set("stop", isRunning);
        RunButtonIcon.Data = GetIcon(isRunning ? "IconStop" : "IconRun");
        RunButtonText.Text = isRunning ? "停止" : "运行";
        ToolTip.SetTip(RunButton, isRunning ? "停止当前运行 (F5)" : "执行完整测试序列 (F5)");
        StatusDot.Classes.Set("status-running", isRunning);
        if (isRunning)
        {
            SetStatus("运行中…", StatusKind.Running);
        }
    }

    private void SetStatus(string text, StatusKind kind = StatusKind.Info)
    {
        StatusText.Text = text;
        var resourceKey = kind switch
        {
            StatusKind.Running or StatusKind.Warning => "AppWarningBrush",
            StatusKind.Success => "AppSuccessBrush",
            StatusKind.Error => "AppDangerBrush",
            _ => "AppAccentBrush"
        };
        if (Application.Current?.TryFindResource(resourceKey, out var resource) == true && resource is IBrush brush)
        {
            StatusDot.Fill = brush;
        }
    }

    private void MarkDirty()
    {
        _isDirty = true;
        if (_currentDocument is not null)
        {
            _currentDocument.IsDirty = true;
            _currentDocument.Revision++;
        }
        UpdateWindowTitle();
    }

    private void UpdateWindowTitle()
    {
        if (TopLevel.GetTopLevel(this) is not Window window)
        {
            return;
        }

        var displayName = _currentDocument?.FilePath is { Length: > 0 } path
            ? Path.GetFileName(path)
            : _sequence.Name;
        window.Title = $"测试序列编辑器 — {displayName}{(_isDirty ? " *" : string.Empty)}";
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AttachClosingHandler();
        UpdateWindowTitle();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DetachClosingHandler();
        base.OnDetachedFromVisualTree(e);
    }

    private void ThemeToggle_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Application.Current is null)
        {
            return;
        }

        Application.Current.RequestedThemeVariant =
            Application.Current.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark
                ? Avalonia.Styling.ThemeVariant.Light
                : Avalonia.Styling.ThemeVariant.Dark;
        SyncThemeToggleIcon();
    }

    private void SyncThemeToggleIcon()
    {
        // 暗色主题下显示太阳（点击切回亮色），反之显示月亮
        var isDark = Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        ThemeToggleIcon.Data = GetIcon(isDark ? "IconSun" : "IconMoon");
    }

    private void ReportOperationFailure(string operation, Exception exception)
    {
        SetStatus(operation, StatusKind.Error);
        AppendLog($"{operation}：{exception.Message}");
    }

    private void NumericSourceUnitBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_updating && _selectedItem is not null)
        {
            var sourceUnit = EmptyToNull(NumericSourceUnitBox.Text);
            if (!string.Equals(_selectedItem.VerdictSource.SourceUnit, sourceUnit, StringComparison.Ordinal))
            {
                _selectedItem.VerdictSource.SourceUnit = sourceUnit;
                MarkDirty();
            }
        }
    }

    private void NumericUnitBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_updating && _selectedItem is not null)
        {
            var unit = EmptyToNull(NumericUnitBox.Text);
            if (!string.Equals(_selectedItem.VerdictSource.Unit, unit, StringComparison.Ordinal))
            {
                _selectedItem.VerdictSource.Unit = unit;
                MarkDirty();
            }
        }
    }

    private void StringModeCombo_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_updating && _selectedItem is not null)
        {
            var mode = SelectedOptionValue<StringJudgeMode>(StringModeCombo);
            if (_selectedItem.VerdictSource.StringMode == mode)
            {
                return;
            }

            _selectedItem.VerdictSource.StringMode = mode;
            MarkDirty();
        }
    }

    private void ExpectedStringBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_updating && _selectedItem is not null &&
            !string.Equals(_selectedItem.VerdictSource.ExpectedString, ExpectedStringBox.Text, StringComparison.Ordinal))
        {
            _selectedItem.VerdictSource.ExpectedString = ExpectedStringBox.Text;
            MarkDirty();
        }
    }

}
