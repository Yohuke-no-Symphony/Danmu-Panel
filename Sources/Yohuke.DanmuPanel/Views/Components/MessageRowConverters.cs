using Avalonia.Data.Converters;

namespace Yohuke.DanmuPanel.Views.Components;

internal static class MessageRowConverters
{
    public static IMultiValueConverter AllowInline { get; } =
        new FuncMultiValueConverter<string, bool>(texts => texts.All(text => text is not null && text.IndexOfAny(['\r', '\n']) < 0));
}
