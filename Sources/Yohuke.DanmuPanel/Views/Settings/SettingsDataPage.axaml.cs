using Avalonia;
using Avalonia.Controls;
using Yohuke.DanmuPanel.ViewModels.Settings;

namespace Yohuke.DanmuPanel.Views.Settings;

/// <summary>设置窗口的数据迁移、清理与缓存操作页面。</summary>
public sealed partial class SettingsDataPage : UserControl
{
    /// <summary>加载页面布局，业务状态由宿主绑定。</summary>
    public SettingsDataPage()
    {
        InitializeComponent();
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is SettingsDialogViewModel model && model.Data.RefreshCacheSizeCommand.CanExecute(null))
        {
            model.Data.RefreshCacheSizeCommand.Execute(null);
        }
    }
}
