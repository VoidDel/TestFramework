using Avalonia.Controls;
using Avalonia.Layout;
using TestFramework.App.Services;

namespace TestFramework.App.Views;

public sealed partial class SequenceEditorView
{
    internal enum UnsavedChangesChoice { Cancel, SaveAll, Discard }

    internal Func<IReadOnlyList<SequenceDocument>, Task<UnsavedChangesChoice>>? UnsavedChangesPrompt { get; set; }
    private Window? _ownerWindow;
    private bool _closePromptOpen;
    private bool _closeApproved;

    private void AttachClosingHandler()
    {
        DetachClosingHandler();
        _ownerWindow = TopLevel.GetTopLevel(this) as Window;
        if (_ownerWindow is not null) _ownerWindow.Closing += OwnerWindowOnClosing;
    }

    private void DetachClosingHandler()
    {
        if (_ownerWindow is not null) _ownerWindow.Closing -= OwnerWindowOnClosing;
        _ownerWindow = null;
    }

    private async void OwnerWindowOnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeApproved) return;
        if (!_isRunning && _runService.PendingRecovery.IsCompleted && !_sequenceDocuments.Any(document => document.IsDirty)) return;
        e.Cancel = true;
        if (_closePromptOpen) return;
        _closePromptOpen = true;
        try
        {
            if (await ConfirmCloseAsync())
            {
                _closeApproved = true;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => (sender as Window)?.Close());
            }
        }
        catch (Exception ex)
        {
            ReportOperationFailure("关闭失败", ex);
        }
        finally
        {
            _closePromptOpen = false;
        }
    }

    internal async Task<bool> ConfirmCloseAsync()
    {
        if (_isRunning || !_runService.PendingRecovery.IsCompleted)
        {
            SetStatus("请先停止运行，等待步骤退出和资源清理完成后再关闭。", StatusKind.Error);
            return false;
        }

        var dirtyDocuments = _sequenceDocuments.Where(document => document.IsDirty).ToArray();
        if (dirtyDocuments.Length == 0) return true;
        var choice = await (UnsavedChangesPrompt?.Invoke(dirtyDocuments) ?? ShowUnsavedChangesPromptAsync(dirtyDocuments));
        if (choice == UnsavedChangesChoice.Cancel) return false;
        if (choice == UnsavedChangesChoice.Discard) return true;

        // Validate all documents before writing any of them. A failed save keeps the window open.
        if (_currentDocument?.IsDirty == true && !ValidateForSaveOrRun()) return false;
        foreach (var document in dirtyDocuments)
        {
            var issues = _validator.Validate(document.Sequence);
            if (issues.Count > 0)
            {
                SetStatus($"序列“{document.Sequence.Name}”校验失败，无法保存。", StatusKind.Error);
                foreach (var issue in issues) AppendLog(issue.ToString());
                return false;
            }
        }

        try
        {
            foreach (var document in dirtyDocuments) await SaveDocumentAsync(document);
            RefreshSequenceCombo();
            return !_sequenceDocuments.Any(document => document.IsDirty);
        }
        catch (Exception ex)
        {
            ReportOperationFailure("保存失败", ex);
            return false;
        }
    }

    private async Task<string> SaveDocumentAsync(SequenceDocument document)
    {
        var revision = document.Revision;
        var path = await _documentStore.SaveAsync(document);
        if (document.Revision == revision) document.IsDirty = false;
        _isDirty = _currentDocument?.IsDirty ?? false;
        UpdateWindowTitle();
        return path;
    }

    private Task<UnsavedChangesChoice> ShowUnsavedChangesPromptAsync(IReadOnlyList<SequenceDocument> documents)
    {
        if (_ownerWindow is null) return Task.FromResult(UnsavedChangesChoice.Cancel);
        var dialog = new Window
        {
            Title = "保存修改",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var (text, choice) in new[] { ("保存全部", UnsavedChangesChoice.SaveAll), ("放弃修改", UnsavedChangesChoice.Discard), ("取消", UnsavedChangesChoice.Cancel) })
        {
            var button = new Button { Content = text };
            button.Click += (_, _) => dialog.Close(choice);
            buttons.Children.Add(button);
        }
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20), Spacing = 20,
            Children =
            {
                new TextBlock { Text = $"有 {documents.Count} 个序列尚未保存：\n" + string.Join("\n", documents.Select(document => document.Sequence.Name)), TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                buttons
            }
        };
        return dialog.ShowDialog<UnsavedChangesChoice>(_ownerWindow);
    }
}
