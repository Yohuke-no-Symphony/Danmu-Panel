using Avalonia.Controls;
using Avalonia.Input;
using Yohuke.DanmuPanel.ViewModels.Workspace;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Views;

/// <summary>通过共享双页弹窗布局承载主播搜索和手动添加表单。</summary>
public sealed partial class RoomEditorSurface : UserControl
{
    /// <summary>加载加房表面。</summary>
    public RoomEditorSurface()
    {
        InitializeComponent();
        var dialog = this.FindControl<SearchAndManualDialogSurface>("LookupDialog")!;
        DataContextChanged += (_, _) =>
        {
            var editing = DataContext is RoomEditorViewModel { IsExisting: true };
            dialog.IsSearchEnabled = !editing;
            dialog.SelectedMode = editing ? SearchAndManualDialogMode.Manual : SearchAndManualDialogMode.Search;
        };
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
