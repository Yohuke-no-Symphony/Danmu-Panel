using Avalonia.Controls;
using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.Views.Components;

internal static class MessageFeedLayout
{
    internal static Grid CreateGrid(MessageKind kind, bool canResize = true)
    {
        if (kind == MessageKind.SuperChat)
        {
            var grid = new Grid();
            grid.RowDefinitions = new("Auto,Auto");
            grid.Classes.Add("scFeedLayout");
            return grid;
        }
        return new MessageFeedColumnLayout(kind, canResize);
    }

    internal static Grid CreateHeaderGrid()
    {
        return new() { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
    }
}
