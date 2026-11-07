using Avalonia.Controls;
using Avalonia.Input;
using Yohuke.DanmuPanel.ViewModels.Settings;

namespace Yohuke.DanmuPanel.Views.Settings;

/// <summary>弹窗内的特别关注用户搜索与手动添加表单。</summary>
public sealed partial class SpecialAttentionSearchSurface : UserControl
{
    /// <summary>加载用户查找布局。</summary>
    public SpecialAttentionSearchSurface()
    {
        InitializeComponent();
    }

    private void Search_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is SpecialAttentionEditorViewModel model && model.SearchCommand.CanExecute(null))
        {
            e.Handled = true;
            model.SearchCommand.Execute(null);
        }
    }
}
