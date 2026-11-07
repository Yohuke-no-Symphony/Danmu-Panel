using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yohuke.DanmuPanel.Views.Settings;

namespace Yohuke.DanmuPanel.Tests.Settings;

/// <summary>验证设置布局子控件显示插槽内容并保留宿主的数据上下文。</summary>
public sealed class SettingsLayoutSurfaceTests
{
    /// <summary>卡片和设置行的模板内容与外部编辑器可以同时显示且不会替换父上下文。</summary>
    [AvaloniaFact]
    public void CardAndRowPresentTheirEditorsWithoutReplacingTheHostContext()
    {
        var context = new object();
        var input = new TextBox { Text = "draft" };
        var row = new SettingsRow { Label = "Model", Editor = input };
        var card = new SettingsCard { Title = "Translation", Body = row, DataContext = context };
        var window = new Window { Width = 600, Height = 300, Content = card };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.IsType<Border>(card.Content);
            Assert.IsType<Grid>(row.Content);
            Assert.Contains(input, card.GetVisualDescendants());
            Assert.Same(context, row.DataContext);
            Assert.Same(context, input.DataContext);
            Assert.True(input.Bounds.Width > 0);
            Assert.Contains(card.GetVisualDescendants().OfType<TextBlock>(), label => label.Text == "Translation");
            Assert.Contains(card.GetVisualDescendants().OfType<TextBlock>(), label => label.Text == "Model");
        }
        finally
        {
            window.Close();
        }
    }
}
