using Avalonia.Controls;
using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.Views.Components;

internal static class MessageFeedLayout
{
    internal static Grid CreateGrid(MessageKind kind)
    {
        var grid = new Grid();
        if (kind == MessageKind.SuperChat)
        {
            grid.RowDefinitions = new("Auto,Auto");
            grid.Classes.Add("scFeedLayout");
        }
        else
        {
            grid.ColumnDefinitions = new(kind == MessageKind.Guard ? "3*,2*" : "2*,3*");
            grid.Classes.Add("danmuFeedLayout");
        }
        return grid;
    }

    internal static Grid CreateHeaderGrid()
    {
        return new() { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
    }
}
