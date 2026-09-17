using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using TestFramework.Plugin.Abstractions.UI;
using TestFramework.Plugins.BasicSteps.Settings;

namespace TestFramework.Plugins.BasicSteps.UI;

/// <summary>
/// The settings editors for the built-in step plugins. They ship in this assembly rather than in
/// the plugins themselves so <c>TestFramework.Plugins.BasicSteps.dll</c> stays free of Avalonia and
/// loads in a headless host, and they ship here rather than in the desktop app so the plugin and
/// its UI can move to their own repository together.
/// </summary>
internal static class BasicStepSettingsEditors
{
    internal static Control CreateDelayEditor(object settings, ISettingsEditContext context)
    {
        var typed = (DelayStepSettings)settings;
        var panel = CreatePanel();
        AddIntBox(panel, nameof(DelayStepSettings.DelayMs), "延时 ms", typed.DelayMs, value => typed.DelayMs = value, context);
        return panel;
    }

    internal static Control CreateLogEditor(object settings, ISettingsEditContext context)
    {
        var typed = (LogStepSettings)settings;
        var panel = CreatePanel();
        AddTextBox(panel, nameof(LogStepSettings.Message), "消息", typed.Message, value => typed.Message = value, context);
        return panel;
    }

    internal static Control CreateLimitCheckEditor(object settings, ISettingsEditContext context)
    {
        var typed = (LimitCheckStepSettings)settings;
        var panel = CreatePanel();
        AddDoubleBox(panel, nameof(LimitCheckStepSettings.Value), "测量值", typed.Value, value => typed.Value = value, context);
        AddDoubleBox(panel, nameof(LimitCheckStepSettings.Min), "下限", typed.Min, value => typed.Min = value, context);
        AddDoubleBox(panel, nameof(LimitCheckStepSettings.Max), "上限", typed.Max, value => typed.Max = value, context);
        return panel;
    }

    internal static Control CreateThrowEditor(object settings, ISettingsEditContext context)
    {
        var typed = (ThrowStepSettings)settings;
        var panel = CreatePanel();
        AddTextBox(panel, nameof(ThrowStepSettings.Message), "异常消息", typed.Message, value => typed.Message = value, context);
        return panel;
    }

    private static StackPanel CreatePanel()
    {
        return new StackPanel { Spacing = 4 };
    }

    private static TextBox AddTextBox(
        StackPanel panel,
        string parameterKey,
        string label,
        string value,
        Action<string> onChanged,
        ISettingsEditContext context,
        bool writeRawText = true)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 4) });
        var textBox = new TextBox { Text = GetInitialText(context, parameterKey, value) };
        textBox.TextChanged += (_, _) =>
        {
            var text = textBox.Text ?? string.Empty;
            onChanged(text);
            if (writeRawText)
            {
                SetRawParameter(context, parameterKey, text);
            }

            context.NotifySettingsChanged();
        };
        panel.Children.Add(CreateInputRow(textBox, context));
        return textBox;
    }

    private static TextBox AddIntBox(
        StackPanel panel,
        string parameterKey,
        string label,
        int value,
        Action<int> onChanged,
        ISettingsEditContext context)
    {
        return AddTextBox(panel, parameterKey, label, value.ToString(CultureInfo.InvariantCulture), text =>
        {
            if (IsVariableExpression(text))
            {
                SetRawParameter(context, parameterKey, text);
                return;
            }

            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                onChanged(parsed);
                SetRawParameter(context, parameterKey, parsed);
            }
        }, context, writeRawText: false);
    }

    private static TextBox AddDoubleBox(
        StackPanel panel,
        string parameterKey,
        string label,
        double value,
        Action<double> onChanged,
        ISettingsEditContext context)
    {
        return AddTextBox(panel, parameterKey, label, value.ToString(CultureInfo.InvariantCulture), text =>
        {
            if (IsVariableExpression(text))
            {
                SetRawParameter(context, parameterKey, text);
                return;
            }

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                onChanged(parsed);
                SetRawParameter(context, parameterKey, parsed);
            }
        }, context, writeRawText: false);
    }

    private static Control CreateInputRow(TextBox textBox, ISettingsEditContext context)
    {
        if (context is not IParameterBindingEditContext bindingContext || bindingContext.Variables.Count == 0)
        {
            return textBox;
        }

        var combo = new ComboBox
        {
            Width = 130,
            ItemsSource = bindingContext.Variables.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList()
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is string variableName)
            {
                textBox.Text = "${" + variableName + "}";
                combo.SelectedItem = null;
            }
        };
        Grid.SetColumn(combo, 1);

        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children =
            {
                textBox,
                combo
            }
        };
    }

    private static string GetInitialText(ISettingsEditContext context, string parameterKey, object value)
    {
        if (context is IParameterBindingEditContext bindingContext)
        {
            var raw = bindingContext.GetParameterValue(parameterKey);
            if (raw is not null)
            {
                return SettingsValueConverter.Format(raw);
            }
        }

        return SettingsValueConverter.Format(value);
    }

    private static void SetRawParameter(ISettingsEditContext context, string parameterKey, object? value)
    {
        if (context is IParameterBindingEditContext bindingContext)
        {
            bindingContext.SetParameterValue(parameterKey, value);
        }
    }

    private static bool IsVariableExpression(string text)
    {
        return text.Contains("${", StringComparison.Ordinal);
    }

}

/// <summary>Base for the built-in editors; each one only names the plugin it serves.</summary>
public abstract class BasicStepEditorPlugin : IStepSettingsEditorPlugin
{
    public abstract string PluginId { get; }

    public Version PluginVersion { get; } = new(1, 0, 0);

    public abstract Control CreateEditor(object settings, ISettingsEditContext context);
}

public sealed class DelayStepEditorPlugin : BasicStepEditorPlugin
{
    public override string PluginId => "basic.delay";

    public override Control CreateEditor(object settings, ISettingsEditContext context) =>
        BasicStepSettingsEditors.CreateDelayEditor(settings, context);
}

public sealed class LogStepEditorPlugin : BasicStepEditorPlugin
{
    public override string PluginId => "basic.log";

    public override Control CreateEditor(object settings, ISettingsEditContext context) =>
        BasicStepSettingsEditors.CreateLogEditor(settings, context);
}

public sealed class LimitCheckStepEditorPlugin : BasicStepEditorPlugin
{
    public override string PluginId => "basic.limit-check";

    public override Control CreateEditor(object settings, ISettingsEditContext context) =>
        BasicStepSettingsEditors.CreateLimitCheckEditor(settings, context);
}

public sealed class ThrowStepEditorPlugin : BasicStepEditorPlugin
{
    public override string PluginId => "basic.throw";

    public override Control CreateEditor(object settings, ISettingsEditContext context) =>
        BasicStepSettingsEditors.CreateThrowEditor(settings, context);
}
