using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using TestFramework.Abstractions.Models;
using TestFramework.App.Views;
using Xunit;

namespace TestFramework.App.Tests;

[Collection("Avalonia UI")]
public sealed class SequenceEditorViewTests
{
    [Fact]
    public async Task RefreshingVerdictControls_PreservesTheConfiguredStep_AndInvalidNumbersBlockValidation()
    {
        var root = Path.Combine(Path.GetTempPath(), "TestFrameworkTests", Guid.NewGuid().ToString("N"));
        var sequenceDirectory = Path.Combine(root, "sequences");
        var resultDirectory = Path.Combine(root, "results");
        using var session = HeadlessUnitTestSession.StartNew(typeof(SkiaRenderTestApp));
        try
        {
            await session.Dispatch(() =>
            {
                var view = new SequenceEditorView(sequenceDirectory, resultDirectory);
                var verdictCombo = view.FindControl<ComboBox>("VerdictStepCombo")!;
                var initialStep = Assert.IsType<TestStepDefinition>(verdictCombo.SelectedItem);

                InvokePrivate(view, "RefreshVerdictControls");

                Assert.Equal(initialStep.Id, Assert.IsType<TestStepDefinition>(verdictCombo.SelectedItem).Id);
                var sequence = Assert.IsType<TestSequence>(GetPrivateField(view, "_sequence"));
                Assert.Equal(initialStep.Id, Assert.Single(sequence.Items).VerdictSource.StepId);
                sequence.Items[0].VerdictSource.JudgeType = VerdictJudgeType.Numeric;

                var lowerLimit = view.FindControl<TextBox>("NumericLowerBox")!;
                lowerLimit.Text = "4,8";
                InvokePrivate(view, "NumericLowerBox_OnTextChanged", null, null);

                Assert.False((bool)InvokePrivate(view, "ValidateForSaveOrRun")!);
                Assert.Contains("格式无效", view.FindControl<TextBlock>("StatusText")!.Text);
            }, CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PluginPicker_GroupsBuiltInStepsUnderBuiltInCategory()
    {
        var root = Path.Combine(Path.GetTempPath(), "TestFrameworkTests", Guid.NewGuid().ToString("N"));
        using var session = HeadlessUnitTestSession.StartNew(typeof(SkiaRenderTestApp));
        try
        {
            await session.Dispatch(() =>
            {
                var view = new SequenceEditorView(Path.Combine(root, "sequences"), Path.Combine(root, "results"));
                var plugins = Assert.IsAssignableFrom<IReadOnlyList<SequenceEditorView.PluginTreeNode>>(
                    InvokePrivate(view, "BuildPluginTree"));
                var picker = new StepPluginPickerWindow(plugins);
                var pluginTree = picker.FindControl<TreeView>("PluginTree")!;
                var categories = Assert.IsAssignableFrom<IEnumerable<SequenceEditorView.PluginTreeNode>>(pluginTree.ItemsSource).ToList();

                var builtIn = Assert.Single(categories, category => category.Title == "内置");
                Assert.Equal(4, builtIn.Children.Count);
                Assert.All(builtIn.Children, plugin => Assert.NotNull(plugin.Plugin));
            }, CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PointerPressOnTreeViewItem_CapturesDragSourceBeforeSelectionHandlesEvent()
    {
        var root = Path.Combine(Path.GetTempPath(), "TestFrameworkTests", Guid.NewGuid().ToString("N"));
        using var session = HeadlessUnitTestSession.StartNew(typeof(SkiaRenderTestApp));
        try
        {
            await session.Dispatch(() =>
            {
                var view = new SequenceEditorView(Path.Combine(root, "sequences"), Path.Combine(root, "results"));
                var window = new Window
                {
                    Width = 1400,
                    Height = 900,
                    Content = view
                };
                window.Show();

                var sequenceTree = view.FindControl<TreeView>("SequenceTree")!;
                var rootItem = Assert.IsType<TreeViewItem>(sequenceTree.ContainerFromIndex(0));
                rootItem.IsExpanded = true;
                window.GetLayoutManager()?.ExecuteLayoutPass();
                var treeItem = sequenceTree
                    .GetVisualDescendants()
                    .OfType<TreeViewItem>()
                    .First(item => item.DataContext is SequenceEditorView.SequenceTreeNode
                    {
                        Kind: SequenceEditorView.TreeNodeKind.Item
                    });
                var point = treeItem.TranslatePoint(
                    new Point(treeItem.Bounds.Width / 2, treeItem.Bounds.Height / 2),
                    window);
                Assert.NotNull(point);

                window.MouseDown(point.Value, MouseButton.Left, RawInputModifiers.None);

                Assert.NotNull(GetPrivateField(view, "_dragStartEvent"));
                var dragSource = Assert.IsType<SequenceEditorView.SequenceTreeNode>(
                    GetPrivateField(view, "_dragSourceNode"));
                Assert.Equal(SequenceEditorView.TreeNodeKind.Item, dragSource.Kind);

                window.MouseUp(point.Value, MouseButton.Left, RawInputModifiers.None);
                window.Close();
            }, CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task StepEditorWindow_OpensWithoutThrowing_MissingIconResourceRegression()
    {
        // 回归测试：StepEditorWindow 曾引用未定义的 IconSettings 资源，打开即抛异常。
        using var session = HeadlessUnitTestSession.StartNew(typeof(SkiaRenderTestApp));
        await session.Dispatch(() =>
        {
            var step = new TestStepDefinition
            {
                Name = "测试 Step",
                PluginId = "basic.log",
                PluginVersion = "1.0.0",
                OnError = ErrorHandlingMode.Stop,
                Parameters = new Dictionary<string, object?>()
            };
            var window = new StepEditorWindow(
                step,
                new TestFramework.Core.Plugins.PluginRegistry(),
                new TestFramework.App.Services.PluginSettingsEditorRegistry(),
                new Dictionary<string, object?>());

            Assert.Equal("测试 Step", window.FindControl<TextBox>("StepNameBox")!.Text);
            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task DisabledTreeNode_RendersWithReducedOpacity()
    {
        var root = Path.Combine(Path.GetTempPath(), "TestFrameworkTests", Guid.NewGuid().ToString("N"));
        using var session = HeadlessUnitTestSession.StartNew(typeof(SkiaRenderTestApp));
        try
        {
            await session.Dispatch(() =>
            {
                var view = new SequenceEditorView(Path.Combine(root, "sequences"), Path.Combine(root, "results"));
                var window = new Window { Width = 1400, Height = 900, Content = view };
                window.Show();

                var sequence = Assert.IsType<TestSequence>(GetPrivateField(view, "_sequence"));
                sequence.Items[0].Enabled = false;
                InvokePrivate(view, "RefreshSequenceTree");

                // 触发展开与布局，让树节点容器完成实现
                var layoutManager = window.GetLayoutManager();
                layoutManager?.ExecuteLayoutPass();
                layoutManager?.ExecuteLayoutPass();

                // 通过行内文本精确定位行模板根 Grid（TreeViewItem 模板内部 Grid 会继承相同 DataContext，不能直接按 DataContext 匹配）
                var nameBlock = view
                    .GetVisualDescendants()
                    .OfType<TextBlock>()
                    .FirstOrDefault(text => text.Text == sequence.Items[0].Name && text.Parent is Grid);
                var disabledRow = Assert.IsType<Grid>(nameBlock?.Parent);
                Assert.True(disabledRow.Opacity < 1,
                    $"禁用节点的整行应变灰。Opacity={disabledRow.Opacity}");

                window.Close();
            }, CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task FreshView_IsNotMarkedDirty()
    {
        var root = Path.Combine(Path.GetTempPath(), "TestFrameworkTests", Guid.NewGuid().ToString("N"));
        using var session = HeadlessUnitTestSession.StartNew(typeof(SkiaRenderTestApp));
        try
        {
            await session.Dispatch(() =>
            {
                var view = new SequenceEditorView(Path.Combine(root, "sequences"), Path.Combine(root, "results"));
                var window = new Window { Width = 1400, Height = 900, Content = view };
                window.Show();
                window.GetLayoutManager()?.ExecuteLayoutPass();
                window.GetLayoutManager()?.ExecuteLayoutPass();

                Assert.False((bool)GetPrivateField(view, "_isDirty")!,
                    "刚加载的序列不应带未保存标记。");

                window.Close();
            }, CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// 为视觉帧捕获提供带真实渲染管线的 AppBuilder（UseHeadlessDrawing=false 才会真正渲染）。
    /// </summary>
    public static class SkiaRenderTestApp
    {
        public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }

    [Fact]
    public async Task Views_RenderWithoutThrowing_InLightAndDarkThemes()
    {
        // 渲染冒烟测试：真实 Skia 管线下，主视图与 Step 编辑器在明/暗主题中均应正常出帧。
        // 同时覆盖 IconSettings 缺失导致 Step 编辑器崩溃的回归。
        var root = Path.Combine(Path.GetTempPath(), "TestFrameworkTests", Guid.NewGuid().ToString("N"));
        using var session = HeadlessUnitTestSession.StartNew(typeof(SkiaRenderTestApp));
        try
        {
            await session.Dispatch(() =>
            {
                var view = new SequenceEditorView(Path.Combine(root, "sequences"), Path.Combine(root, "results"));
                var window = new Window
                {
                    Width = 1440,
                    Height = 900,
                    Title = "测试序列编辑器",
                    Content = view,
                    Background = Avalonia.Media.Brushes.White
                };
                window.Show();
                var layoutManager = window.GetLayoutManager();
                layoutManager?.ExecuteLayoutPass();
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();

                Assert.NotNull(window.CaptureRenderedFrame());

                // 禁用测试项 → 树节点应变灰
                var sequence = Assert.IsType<TestSequence>(GetPrivateField(view, "_sequence"));
                sequence.Items[0].Enabled = false;
                InvokePrivate(view, "RefreshSequenceTree");
                layoutManager?.ExecuteLayoutPass();
                Assert.NotNull(window.CaptureRenderedFrame());
                sequence.Items[0].Enabled = true;
                InvokePrivate(view, "RefreshSequenceTree");

                // 切换到暗色主题
                Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
                layoutManager?.ExecuteLayoutPass();
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Assert.NotNull(window.CaptureRenderedFrame());
                Application.Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;

                // Step 编辑器窗口（IconSettings 崩溃回归）
                var step = sequence.Items[0].MainSteps.Last();
                var editor = new StepEditorWindow(
                    step,
                    new TestFramework.Core.Plugins.PluginRegistry(),
                    new TestFramework.App.Services.PluginSettingsEditorRegistry(),
                    new Dictionary<string, object?>());
                editor.Show();
                layoutManager?.ExecuteLayoutPass();
                Assert.NotNull(editor.CaptureRenderedFrame());
                editor.Close();

                window.Close();
            }, CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static object? InvokePrivate(object target, string methodName, params object?[]? arguments)
    {
        return target.GetType()
            .GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(target, arguments);
    }

    private static object? GetPrivateField(object target, string fieldName)
    {
        return target.GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(target);
    }
}
