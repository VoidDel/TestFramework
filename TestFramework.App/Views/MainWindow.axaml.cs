using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace TestFramework.App.Views;

public sealed partial class MainWindow : Window
{
    private Size _normalWindowSize = new(1280, 820);
    private Size _pendingNormalWindowSize = new(1280, 820);
    private bool _isRestoringNormalWindowSize;
    private readonly DispatcherTimer _normalSizeCaptureTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };

    public MainWindow()
    {
        InitializeComponent();

        _normalSizeCaptureTimer.Tick += OnNormalSizeCaptureTimerTick;
        Resized += OnMainWindowResized;
        PropertyChanged += OnMainWindowPropertyChanged;
    }

    private void OnMainWindowResized(object? sender, WindowResizedEventArgs e)
    {
        if (_isRestoringNormalWindowSize ||
            WindowState != WindowState.Normal ||
            e.ClientSize.Width <= 0 ||
            e.ClientSize.Height <= 0 ||
            e.Reason is WindowResizeReason.Application or WindowResizeReason.Layout or WindowResizeReason.DpiChange)
        {
            return;
        }

        // 最大化时，部分平台会先发送尺寸变化、随后才更新 WindowState。
        // 延迟确认可避免把最大化尺寸误记成普通窗口尺寸。
        _pendingNormalWindowSize = e.ClientSize;
        _normalSizeCaptureTimer.Stop();
        _normalSizeCaptureTimer.Start();
    }

    private void OnNormalSizeCaptureTimerTick(object? sender, EventArgs e)
    {
        _normalSizeCaptureTimer.Stop();
        if (!_isRestoringNormalWindowSize && WindowState == WindowState.Normal)
        {
            _normalWindowSize = _pendingNormalWindowSize;
        }
    }

    private void OnMainWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != WindowStateProperty)
        {
            return;
        }

        _normalSizeCaptureTimer.Stop();
        if (WindowState != WindowState.Normal)
        {
            return;
        }

        var restoreSize = _normalWindowSize;
        _isRestoringNormalWindowSize = true;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (WindowState == WindowState.Normal)
                {
                    Width = Math.Max(MinWidth, restoreSize.Width);
                    Height = Math.Max(MinHeight, restoreSize.Height);
                }
            }
            finally
            {
                Dispatcher.UIThread.Post(
                    () => _isRestoringNormalWindowSize = false,
                    DispatcherPriority.Background);
            }
        }, DispatcherPriority.Background);
    }
}
