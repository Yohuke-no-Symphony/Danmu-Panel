using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Yohuke.DanmuPanel.Views;

/// <summary>声明测试消息表单和每个停靠宿主独立使用的标题操作。</summary>
public sealed partial class TestMessagesResources : ResourceDictionary
{
    /// <summary>加载编译后的表单与标题模板。</summary>
    public TestMessagesResources()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
