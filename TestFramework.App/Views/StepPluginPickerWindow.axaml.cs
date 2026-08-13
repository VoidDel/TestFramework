using Avalonia.Controls;
using TestFramework.Abstractions.Plugins;

namespace TestFramework.App.Views;

public sealed partial class StepPluginPickerWindow : Window
{
    public StepPluginPickerWindow()
    {
        InitializeComponent();
    }

    public StepPluginPickerWindow(IEnumerable<SequenceEditorView.PluginTreeNode> plugins)
        : this()
    {
        PluginTree.ItemsSource = plugins;
    }

    private void Add_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        CloseWithSelectedPlugin();
    }

    private void Cancel_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close(null);
    }

    private void PluginTree_OnDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        CloseWithSelectedPlugin();
    }

    private void CloseWithSelectedPlugin()
    {
        if (PluginTree.SelectedItem is SequenceEditorView.PluginTreeNode { Plugin: { } plugin })
        {
            Close(plugin);
        }
    }
}
