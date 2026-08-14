using System.Globalization;
using Avalonia.Data.Converters;

namespace TestFramework.App.Converters;

/// <summary>
/// 将布尔值转换为透明度：true 时降低透明度（用于禁用状态），false 时完全不透明。
/// </summary>
public sealed class DisabledOpacityConverter : IValueConverter
{
    public static DisabledOpacityConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? 0.45 : 1.0;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
