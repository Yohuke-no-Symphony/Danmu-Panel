using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Yohuke.DanmuPanel.ViewModels.Settings;
using Yohuke.DanmuPanel.Views.Components;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Settings;

/// <summary>验证颜色草稿与 AXAML 输入之间的装载、验证和模型替换契约。</summary>
public sealed class ColorInputBindingTests
{
    /// <summary>装载及默认色刷新不发布编辑事件，无效草稿保留原文和上次有效预览。</summary>
    [AvaloniaFact]
    public void LoadingAndRefreshingDefaultsPreserveDraftAndDoNotPublishChanges()
    {
        var model = new ColorInputViewModel();
        var changes = new List<string?>();
        model.ValueChanged += changes.Add;

        model.Load("#112233", Colors.White);
        model.RawText = "#FF";
        model.RefreshDefault(Colors.Black);

        Assert.Equal("#FF", model.RawText);
        Assert.False(model.IsValid);
        Assert.True(model.HasError);
        Assert.Equal(Color.Parse("#112233"), model.PreviewColor);
        Assert.Equal("#000000", model.Watermark);
        Assert.Empty(changes);

        model.Load("#80112233", Colors.White);

        Assert.True(model.IsValid);
        Assert.Equal("#80112233", model.RawText);
        Assert.Equal(Color.Parse("#80112233"), model.PreviewColor);
        Assert.Empty(changes);

        model.RawText = " #aabbcc ";

        Assert.Equal(" #aabbcc ", model.RawText);
        Assert.Equal("#AABBCC", Assert.Single(changes));
    }

    /// <summary>输入控件换绑后只更新当前草稿，原草稿保留且装载不会反向写入配置。</summary>
    [AvaloniaFact]
    public void ReplacingModelRoutesBindingsAndNotificationsToCurrentDraft()
    {
        var input = new ColorInput();
        var previous = input.Model;
        previous.Load("#112233", Colors.White);
        var current = new ColorInputViewModel();
        current.Load("#445566", Colors.Black);
        var changes = new List<string?>();
        input.ValueChanged += changes.Add;
        var window = new Window { Width = 320, Height = 150, Content = input };
        try
        {
            window.Show();
            input.Model = current;
            Dispatcher.UIThread.RunJobs();
            var text = input.GetLogicalDescendants().OfType<TextBox>().Single(control => control.Name == "ColorHexInput");
            Assert.Equal("#445566", text.Text);
            Assert.Empty(changes);

            text.Text = "#778899";
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("#778899", current.RawText);
            Assert.Equal("#112233", previous.RawText);
            Assert.Equal("#778899", Assert.Single(changes));

            previous.RawText = "#ABCDEF";

            Assert.Single(changes);
            Assert.Equal("#778899", text.Text);
        }
        finally
        {
            window.Close();
        }
    }
}
