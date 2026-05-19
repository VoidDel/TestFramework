using Avalonia.Controls;

namespace TestFramework.Plugin.Abstractions.UI;

public interface ITestStepSettingsEditorProvider
{
    Control CreateEditor(object settings, ISettingsEditContext context);
}
