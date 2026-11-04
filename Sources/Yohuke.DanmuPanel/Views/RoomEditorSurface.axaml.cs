using Avalonia.Controls;
using Avalonia.Input;
using Yohuke.DanmuPanel.ViewModels.Workspace;

namespace Yohuke.DanmuPanel.Views;

/// <summary>复用 Neo 主播搜索、结果卡片和手动添加表单。</summary>
public sealed partial class RoomEditorSurface : UserControl
{
    /// <summary>加载加房表面。</summary>
    public RoomEditorSurface()
    {
        InitializeComponent();
    }

    private void LiveRoomSearch_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is RoomEditorViewModel model && model.SearchLiveRoomsCommand.CanExecute(null))
        {
            e.Handled = true;
            model.SearchLiveRoomsCommand.Execute(null);
        }
    }
}
