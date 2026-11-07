using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;

namespace Yohuke.DanmuPanel.Views.Components;

/// <summary>提供可为每个宿主独立创建的消息面板标题和菜单模板。</summary>
public sealed partial class MessageFeedTemplates : ResourceDictionary
{
    /// <summary>加载消息面板的声明式模板资源。</summary>
    public MessageFeedTemplates()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private async void ReadTabs_OnSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        args.Handled = true;
        if (sender is TabStrip { SelectedIndex: 0 or 1, DataContext: MessageFeedView view } tabs)
        {
            await view.SelectReadTabAsync(tabs.SelectedIndex);
        }
    }
}
