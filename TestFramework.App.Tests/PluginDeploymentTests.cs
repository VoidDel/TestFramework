using Avalonia.Controls;
using Avalonia.Headless;
using TestFramework.App.Services;
using TestFramework.Core.Plugins;
using TestFramework.Core.Resources;
using Xunit;

namespace TestFramework.App.Tests;

/// <summary>
/// Loads the built-in plugin package the way a third-party plugin folder arrives: with the
/// Avalonia assemblies that <c>dotnet publish</c> puts beside a settings-editor plugin. Those must
/// resolve to the host's Avalonia, or every editor in the folder fails with "does not have an
/// implementation" because its <c>CreateEditor</c> returns a <c>Control</c> of another identity.
/// </summary>
[Collection(AvaloniaUiCollection.Name)]
public sealed class PluginDeploymentTests
{
    private readonly HeadlessUnitTestSession _session;

    public PluginDeploymentTests(AvaloniaTestSession session) => _session = session.Session;

    [Fact]
    public async Task EditorPluginShippedWithItsOwnAvaloniaCopies_StillLoadsAndBuildsEditors()
    {
        var root = Path.Combine(Path.GetTempPath(), "TestFrameworkTests", Guid.NewGuid().ToString("N"));
        var pluginDirectory = Path.Combine(root, "Plugins", "BasicSteps");
        Directory.CreateDirectory(pluginDirectory);
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Plugins", "BasicSteps"), "*.dll"))
        {
            File.Copy(file, Path.Combine(pluginDirectory, Path.GetFileName(file)));
        }

        foreach (var name in new[] { "Avalonia.Base.dll", "Avalonia.Controls.dll" })
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, name), Path.Combine(pluginDirectory, name));
        }

        try
        {
            await _session.Dispatch(() =>
            {
                var steps = new PluginRegistry();
                var editors = new PluginSettingsEditorRegistry();
                var report = new PluginDirectoryLoader(steps, new ResourcePluginRegistry(), [new SettingsEditorPluginHandler(editors)])
                    .LoadFromDirectory(Path.Combine(root, "Plugins"));

                Assert.Empty(report.Failures.Select(failure => failure.Message));
                var delay = steps.GetRequired("basic.delay");
                Assert.True(editors.TryCreateEditor(
                    "basic.delay",
                    delay.Descriptor.Version.ToString(),
                    delay.CreateDefaultSettings(),
                    new SettingsEditContext(() => { }, new Dictionary<string, object?>(), _ => null, (_, _) => { }),
                    out var editor));
                Assert.IsAssignableFrom<Control>(editor);
                return true;
            }, CancellationToken.None);
        }
        finally
        {
            // The plugin copies stay loaded and locked for the life of the process.
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
