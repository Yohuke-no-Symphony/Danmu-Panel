using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.Views.Components;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Windowing;

/// <summary>验证紧凑正文的字体颜色绑定和悬停动画生命周期。</summary>
public sealed class HoverMarqueeTextBlockTests
{
    /// <summary>正文显式跟随外层字体和前景色，并保持左对齐垂直居中。</summary>
    [AvaloniaFact]
    public void InnerTextFollowsFontAndForeground()
    {
        var marquee = new HoverMarqueeTextBlock { Text = "compact text", FontSize = 12, Foreground = Brushes.White };
        var window = new Window { Width = 240, Height = 100, Content = marquee };
        try
        {
            window.Show();
            Pump(window);
            var text = Assert.Single(marquee.GetVisualDescendants().OfType<EmoticonTextBlock>());
            Assert.Equal(12, text.FontSize);
            Assert.Same(Brushes.White, text.Foreground);
            Assert.Equal(HorizontalAlignment.Left, text.HorizontalAlignment);
            Assert.Equal(VerticalAlignment.Center, text.VerticalAlignment);
            Assert.Equal(TextWrapping.NoWrap, text.TextWrapping);
            marquee.FontSize = 18;
            marquee.Foreground = Brushes.CornflowerBlue;
            Pump(window);
            Assert.Equal(18, text.FontSize);
            Assert.Same(Brushes.CornflowerBlue, text.Foreground);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>离开、换文、表情刷新、隐藏和移除后清除旧偏移。</summary>
    [AvaloniaTheory]
    [InlineData("exit")]
    [InlineData("text")]
    [InlineData("emoticons")]
    [InlineData("hidden")]
    [InlineData("ancestor")]
    [InlineData("detach")]
    public async Task AnimationResetsWhenItsContentOrVisibilityChanges(string change)
    {
        var marquee = new HoverMarqueeTextBlock
        {
            Text = string.Concat(Enumerable.Repeat("overflowing text ", 25)),
            Height = 32,
            Margin = new(10),
            VerticalAlignment = VerticalAlignment.Top
        };
        var parent = new Border { Child = marquee };
        var window = new Window { Width = 220, Height = 160, Content = parent };
        try
        {
            window.Show();
            Pump(window);
            var viewer = Assert.Single(marquee.GetVisualDescendants().OfType<ScrollViewer>());
            Assert.True(viewer.Extent.Width > viewer.Viewport.Width);
            var point = marquee.TranslatePoint(new Point(20, 12), window)!.Value;
            window.MouseMove(point);
            await WaitUntilAsync(() => viewer.Offset.X > 0, window);
            switch (change)
            {
                case "exit":
                    window.MouseMove(new(210, 150));
                    break;
                case "text":
                    marquee.Text = "short";
                    break;
                case "emoticons":
                    marquee.Emoticons = [];
                    break;
                case "hidden":
                    marquee.IsVisible = false;
                    break;
                case "ancestor":
                    parent.IsVisible = false;
                    break;
                case "detach":
                    parent.Child = null;
                    break;
            }
            await WaitUntilAsync(() => viewer.Offset.X == 0, window);
            await Task.Delay(60);
            Pump(window);
            Assert.Equal(0, viewer.Offset.X);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>自动开关无需悬停即可启动，关闭后复位并保留原有悬停阅读。</summary>
    [AvaloniaFact]
    public async Task AutoScrollToggleStartsWithoutHoverAndRestoresHoverBehavior()
    {
        var marquee = new HoverMarqueeTextBlock
        {
            Text = new string('长', 80), Height = 32, Margin = new(10),
            VerticalAlignment = VerticalAlignment.Top
        };
        var window = new Window { Width = 220, Height = 160, Content = marquee };
        try
        {
            window.Show();
            window.MouseMove(new(210, 150));
            Pump(window);
            var viewer = Assert.Single(marquee.GetVisualDescendants().OfType<ScrollViewer>());
            await Task.Delay(450);
            Pump(window);
            Assert.Equal(0, viewer.Offset.X);

            marquee.AutoScroll = true;
            await WaitUntilAsync(() => viewer.Offset.X > 0, window);
            var previousOffset = viewer.Offset.X;
            window.MouseMove(marquee.TranslatePoint(new Point(20, 12), window)!.Value);
            window.MouseMove(new(210, 150));
            Pump(window);
            Assert.True(viewer.Offset.X >= previousOffset);

            marquee.AutoScroll = false;
            Pump(window);
            Assert.Equal(0, viewer.Offset.X);
            await Task.Delay(450);
            Pump(window);
            Assert.Equal(0, viewer.Offset.X);

            window.MouseMove(marquee.TranslatePoint(new Point(20, 12), window)!.Value);
            await WaitUntilAsync(() => viewer.Offset.X > 0, window);
            window.MouseMove(new(210, 150));
            Pump(window);
            Assert.Equal(0, viewer.Offset.X);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>隐藏、移除或滚出视口会停止自动滚动，恢复可见后重新开始。</summary>
    [AvaloniaTheory]
    [InlineData("hidden")]
    [InlineData("ancestor")]
    [InlineData("detach")]
    [InlineData("offscreen")]
    public async Task AutoScrollStopsOutsideVisibleViewportAndRestartsWhenShown(string change)
    {
        var marquee = new HoverMarqueeTextBlock
        {
            Text = new string('长', 80), AutoScroll = true, Height = 32, Margin = new(10)
        };
        var parent = new Border { Child = marquee };
        var outerViewer = new ScrollViewer
        {
            Content = new StackPanel { Children = { parent, new Border { Height = 400 } } }
        };
        var window = new Window { Width = 220, Height = 160, Content = outerViewer };
        try
        {
            window.Show();
            Pump(window);
            var viewer = Assert.Single(marquee.GetVisualDescendants().OfType<ScrollViewer>());
            await WaitUntilAsync(() => viewer.Offset.X > 0, window);
            switch (change)
            {
                case "hidden":
                    marquee.IsVisible = false;
                    break;
                case "ancestor":
                    parent.IsVisible = false;
                    break;
                case "detach":
                    parent.Child = null;
                    break;
                case "offscreen":
                    outerViewer.Offset = new(0, 250);
                    break;
            }
            await WaitUntilAsync(() => viewer.Offset.X == 0, window);
            await Task.Delay(450);
            Pump(window);
            Assert.Equal(0, viewer.Offset.X);

            switch (change)
            {
                case "hidden":
                    marquee.IsVisible = true;
                    break;
                case "ancestor":
                    parent.IsVisible = true;
                    break;
                case "detach":
                    parent.Child = marquee;
                    break;
                case "offscreen":
                    outerViewer.Offset = default;
                    break;
            }
            await WaitUntilAsync(() => viewer.Offset.X > 0, window);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>等宽的新内容也从头重新滚动，短文本停止且恢复长文本后继续。</summary>
    [AvaloniaFact]
    public async Task AutoScrollRestartsForEqualWidthReplacementAndStopsForShortText()
    {
        var marquee = new HoverMarqueeTextBlock { Text = new string('甲', 80), AutoScroll = true };
        var window = new Window { Width = 220, Height = 100, Content = marquee };
        try
        {
            window.Show();
            Pump(window);
            var viewer = Assert.Single(marquee.GetVisualDescendants().OfType<ScrollViewer>());
            await WaitUntilAsync(() => viewer.Offset.X > 0, window);
            var previousExtent = viewer.Extent.Width;

            marquee.Text = new string('乙', 80);
            Pump(window);
            Assert.Equal(previousExtent, viewer.Extent.Width, 3);
            Assert.Equal(0, viewer.Offset.X);
            await WaitUntilAsync(() => viewer.Offset.X > 0, window);

            marquee.Text = "短";
            Pump(window);
            Assert.Equal(0, viewer.Offset.X);
            await Task.Delay(450);
            Pump(window);
            Assert.Equal(0, viewer.Offset.X);

            marquee.Text = new string('长', 80);
            await WaitUntilAsync(() => viewer.Offset.X > 0, window);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>尺寸变化后只在文字溢出时滚动，无需再次移动鼠标。</summary>
    [AvaloniaFact]
    public async Task AutoScrollStopsWhenTextFitsAndRestartsAfterViewportShrinks()
    {
        var marquee = new HoverMarqueeTextBlock { Text = new string('宽', 20), AutoScroll = true };
        var window = new Window { Width = 160, Height = 100, Content = marquee };
        try
        {
            window.Show();
            Pump(window);
            var viewer = Assert.Single(marquee.GetVisualDescendants().OfType<ScrollViewer>());
            await WaitUntilAsync(() => viewer.Offset.X > 0, window);

            window.Width = 1000;
            Pump(window);
            Assert.True(viewer.Extent.Width <= viewer.Viewport.Width);
            Assert.Equal(0, viewer.Offset.X);
            await Task.Delay(450);
            Pump(window);
            Assert.Equal(0, viewer.Offset.X);

            window.Width = 160;
            await WaitUntilAsync(() => viewer.Offset.X > 0, window);
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, Window window)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(20);
            Pump(window);
        }
        Assert.True(condition());
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
