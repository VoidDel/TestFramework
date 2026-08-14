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

public sealed class SequenceEditorViewTests
{
    [Fact]
    public async Task RefreshingVerdictControls_PreservesTheConfiguredStep_AndInvalidNumbersBlockValidation()
    {
        var root = Path.Combine(Path.GetTempPath(), "TestFrameworkTests", Guid.NewGuid().ToString("N"));
        var sequenceDirectory = Path.Combine(root, "sequences");
        var resultDirectory = Path.Combine(root, "results");
        using var session = HeadlessUnitTestSession.StartNew(typeof(App));
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
        using var session = HeadlessUnitTestSession.StartNew(typeof(App));
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
        using var session = HeadlessUnitTestSession.StartNew(typeof(App));
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
