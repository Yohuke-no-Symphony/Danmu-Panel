using Avalonia.Data.Converters;

namespace Yohuke.DanmuPanel.Layouts;

internal static class WorkbenchDockConverters
{
    public static IValueConverter HasItems { get; } = new FuncValueConverter<int, bool>(count => count > 0);
}
