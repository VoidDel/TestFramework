using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TestFramework.Abstractions.Models;
using TestFramework.App.Services;
using TestFramework.App.Views;
using TestFramework.Core.Plugins;
using TestFramework.Plugins.BasicSteps;
using Xunit;

namespace TestFramework.App.Tests;

[Collection("Avalonia UI")]
public sealed class EditorSafetyTests
{
    [Fact]
    public async Task StopButton_MouseClickCancelsAndPersistsPartialResult()
    {
        await WithView(async (view, window, directory) =>
        {
            var sequence = Field<TestSequence>(view, "_sequence");
            sequence.Items[0].InitSteps.Clear();
            sequence.Items[0].MainSteps = [new TestStepDefinition
            {
                Id = "delay", Name = "Delay", PluginId = "basic.delay", Parameters = { ["DelayMs"] = 10000 }
            }];
            sequence.Items[0].VerdictSource = new VerdictSource { StepId = "delay" };
            Invoke(view, "RefreshAll");
            Dispatcher.UIThread.RunJobs();
            Invoke(view, "Run_OnClick", null, null);
            Assert.True(Field<bool>(view, "_isRunning"));
            window.GetLayoutManager()?.ExecuteLayoutPass();
            Dispatcher.UIThread.RunJobs();

            var button = view.FindControl<Button>("RunButton")!;
            var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            await WaitUntil(() => !Field<bool>(view, "_isRunning"));
            var file = Assert.Single(Directory.GetFiles(Path.Combine(directory, "results"), "*.json"));
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(file));
            Assert.Equal("Cancelled", json.RootElement.GetProperty("Verdict").GetString());
            Assert.Contains("部分结果已保存", view.FindControl<TextBlock>("StatusText")!.Text);
        });
    }

    [Fact]
    public async Task DirtyDocuments_SurviveSwitchingAndCanAllBeSavedOnClose()
    {
        await WithView(async (view, window, directory) =>
        {
            var first = Field<SequenceDocument>(view, "_currentDocument");
            first.Sequence.Name = "First";
            Invoke(view, "MarkDirty");
            Invoke(view, "New_OnClick", null, null);
            var second = Field<SequenceDocument>(view, "_currentDocument");
            second.Sequence.Name = "Second";
            Invoke(view, "SetCurrentDocument", first);
            Assert.True(Field<bool>(view, "_isDirty"));
            view.UnsavedChangesPrompt = documents =>
            {
                Assert.Equal(2, documents.Count);
                return Task.FromResult(SequenceEditorView.UnsavedChangesChoice.Cancel);
            };
            Assert.False(await view.ConfirmCloseAsync());
            Assert.True(first.IsDirty);
            Assert.True(second.IsDirty);

            view.UnsavedChangesPrompt = _ => Task.FromResult(SequenceEditorView.UnsavedChangesChoice.SaveAll);
            Assert.True(await view.ConfirmCloseAsync());
            Assert.False(first.IsDirty);
            Assert.False(second.IsDirty);
            Assert.Equal(2, Directory.GetFiles(Path.Combine(directory, "sequences"), "*.yaml").Length);
        });
    }

    [Fact]
    public async Task SaveConflict_KeepsDirtyDocumentAndBlocksClose()
    {
        await WithView(async (view, window, directory) =>
        {
            var document = Field<SequenceDocument>(view, "_currentDocument");
            await (Task<string>)Invoke(view, "SaveDocumentAsync", document)!;
            Invoke(view, "MarkDirty");
            await File.AppendAllTextAsync(document.FilePath!, "\n");
            view.UnsavedChangesPrompt = _ => Task.FromResult(SequenceEditorView.UnsavedChangesChoice.SaveAll);
            Assert.False(await view.ConfirmCloseAsync());
            Assert.True(document.IsDirty);
            Assert.True(window.IsVisible);
        });
    }

    [Fact]
    public async Task ClosingDirtyWindow_OffersCancelAndDiscard()
    {
        await WithView(async (view, window, directory) =>
        {
            Invoke(view, "MarkDirty");
            view.UnsavedChangesPrompt = _ => Task.FromResult(SequenceEditorView.UnsavedChangesChoice.Cancel);
            window.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.IsVisible);
            view.UnsavedChangesPrompt = _ => Task.FromResult(SequenceEditorView.UnsavedChangesChoice.Discard);
            window.Close();
            await WaitUntil(() => !window.IsVisible);
        });
    }

    [Fact]
    public async Task BoundNumericParameter_RemainsEditableWithoutLoadingLiteralBindingAsNumber()
    {
        await WithView((view, window, directory) =>
        {
            var plugin = new LimitCheckStepPlugin();
            var plugins = new PluginRegistry();
            plugins.Register(plugin);
            var editors = new PluginSettingsEditorRegistry();
            BasicStepSettingsEditors.Register(editors);
            var step = new TestStepDefinition { PluginId = "basic.limit-check", Parameters = { ["Value"] = "${measurement}" } };
            var editor = new StepEditorWindow(step, plugins, editors, new Dictionary<string, object?>());
            editor.Show();
            Assert.NotNull(editor.FindControl<ContentControl>("PluginSettingsContent")!.Content);
            Assert.Equal("${measurement}", step.Parameters["Value"]);
            editor.Close();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task InvalidTimeout_CannotSilentlyDisableTimeout()
    {
        await WithView((view, window, directory) =>
        {
            var step = new TestStepDefinition { TimeoutMs = 100 };
            var editor = new StepEditorWindow(step, new PluginRegistry(), new PluginSettingsEditorRegistry(), new Dictionary<string, object?>());
            editor.Show();
            var input = editor.FindControl<TextBox>("TimeoutBox")!;
            input.Text = "invalid";
            Invoke(editor, "TimeoutBox_OnTextChanged", null, null);
            editor.Close();
            Assert.True(editor.IsVisible);
            Assert.Equal(100, step.TimeoutMs);
            input.Text = "";
            Invoke(editor, "TimeoutBox_OnTextChanged", null, null);
            editor.Close();
            Assert.False(editor.IsVisible);
            Assert.Null(step.TimeoutMs);
            return Task.CompletedTask;
        });
    }

    private static async Task WithView(Func<SequenceEditorView, Window, string, Task> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "TestFrameworkTests", Guid.NewGuid().ToString("N"));
        using var session = HeadlessUnitTestSession.StartNew(typeof(SequenceEditorViewTests.SkiaRenderTestApp));
        try
        {
            await session.Dispatch(async () =>
            {
                var view = new SequenceEditorView(Path.Combine(directory, "sequences"), Path.Combine(directory, "results"));
                var window = new Window { Width = 1440, Height = 900, Content = view };
                window.Show();
                window.GetLayoutManager()?.ExecuteLayoutPass();
                Dispatcher.UIThread.RunJobs();
                try { await test(view, window, directory); }
                finally
                {
                    view.UnsavedChangesPrompt = _ => Task.FromResult(SequenceEditorView.UnsavedChangesChoice.Discard);
                    if (Field<bool>(view, "_isRunning"))
                    {
                        Field<CancellationTokenSource>(view, "_runCts").Cancel();
                        await WaitUntil(() => !Field<bool>(view, "_isRunning"));
                    }
                    window.Close();
                    Dispatcher.UIThread.RunJobs();
                }
                return true;
            }, CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task DeleteSequence_WithoutConfirmation_KeepsTheFileOnDisk()
    {
        await WithView(async (view, window, directory) =>
        {
            var document = Field<SequenceDocument>(view, "_currentDocument");
            await (Task<string>)Invoke(view, "SaveDocumentAsync", document)!;
            var filePath = document.FilePath!;
            Assert.True(File.Exists(filePath));

            var prompted = 0;
            view.DeleteSequencePrompt = _ =>
            {
                prompted++;
                return Task.FromResult(false);
            };
            Invoke(view, "DeleteSequence_OnClick", null, null);
            await WaitUntil(() => prompted > 0);
            Dispatcher.UIThread.RunJobs();

            Assert.True(File.Exists(filePath));
            Assert.Contains(Field<System.Collections.Generic.List<SequenceDocument>>(view, "_sequenceDocuments"), candidate => candidate == document);

            view.DeleteSequencePrompt = _ => Task.FromResult(true);
            Invoke(view, "DeleteSequence_OnClick", null, null);
            await WaitUntil(() => !File.Exists(filePath));
            Dispatcher.UIThread.RunJobs();

            Assert.False(File.Exists(filePath));
        });
    }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static object? Invoke(object target, string name, params object?[] arguments) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);
}
