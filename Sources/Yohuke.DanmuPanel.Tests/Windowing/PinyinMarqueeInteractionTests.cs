using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Views.Components;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Windowing;

/// <summary>验证紧凑正文注音的暂停、续播和手动横向阅读。</summary>
public sealed class PinyinMarqueeInteractionTests
{
    /// <summary>有注音时悬停冻结当前偏移，移开后从当前位置继续自动滚动。</summary>
    [AvaloniaTheory]
    [InlineData(PinyinLayoutMode.Ruby)]
    [InlineData(PinyinLayoutMode.SeparateLine)]
    public async Task HoverPausesAnnotatedTextAndExitResumesTheSameOffset(PinyinLayoutMode layout)
    {
        var marquee = CreateMarquee(layout, true);
        var window = CreateWindow(marquee);
        try
        {
            window.Show();
            window.MouseMove(new(250, 160));
            PinyinTextRenderingTests.Pump(window);
            var viewer = Assert.Single(marquee.GetVisualDescendants().OfType<ScrollViewer>());
            await WaitUntilAsync(() => viewer.Offset.X > 4, window);
            window.MouseMove(marquee.TranslatePoint(new(30, 35), window)!.Value);
            PinyinTextRenderingTests.Pump(window);
            var pausedOffset = viewer.Offset.X;
            await Task.Delay(150);
            PinyinTextRenderingTests.Pump(window);
            Assert.Equal(pausedOffset, viewer.Offset.X);

            window.MouseMove(new(250, 160));
            PinyinTextRenderingTests.Pump(window);
            Assert.True(viewer.Offset.X >= pausedOffset);
            await WaitUntilAsync(() => viewer.Offset.X > pausedOffset + 1, window);
            Assert.Equal(-1, Assert.Single(marquee.GetVisualDescendants().OfType<EmoticonTextBlock>()).HighlightedSourceStart);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>关闭自动滚动时仍可用水平滚轮或 Shift 滚轮访问正文末尾，离开后保留位置。</summary>
    [AvaloniaTheory]
    [InlineData(PinyinLayoutMode.Ruby, false)]
    [InlineData(PinyinLayoutMode.Ruby, true)]
    [InlineData(PinyinLayoutMode.SeparateLine, false)]
    [InlineData(PinyinLayoutMode.SeparateLine, true)]
    public async Task ManualHorizontalScrollingReachesTheTailWithoutEnablingAutoScroll(PinyinLayoutMode layout, bool shiftWheel)
    {
        var marquee = CreateMarquee(layout, false);
        var window = CreateWindow(marquee);
        try
        {
            window.Show();
            PinyinTextRenderingTests.Pump(window);
            var viewer = Assert.Single(marquee.GetVisualDescendants().OfType<ScrollViewer>());
            var point = marquee.TranslatePoint(new(30, 35), window)!.Value;
            window.MouseMove(point);
            window.MouseWheel(point, shiftWheel ? new(0, -100) : new Vector(-100, 0),
                shiftWheel ? RawInputModifiers.Shift : RawInputModifiers.None);
            PinyinTextRenderingTests.Pump(window);
            Assert.False(marquee.AutoScroll);
            Assert.Equal(viewer.Extent.Width - viewer.Viewport.Width, viewer.Offset.X, 2);
            Assert.True(viewer.Offset.X > 0);

            var text = Assert.Single(marquee.GetVisualDescendants().OfType<EmoticonTextBlock>());
            Point tailPoint;
            if (layout == PinyinLayoutMode.Ruby)
            {
                var last = text.GetVisualDescendants().OfType<PinyinRubySyllable>().Last();
                tailPoint = last.OriginalText.TranslatePoint(new(last.OriginalText.Bounds.Width / 2,
                    last.OriginalText.Bounds.Height / 2), window)!.Value;
            }
            else
            {
                var original = Assert.Single(text.GetVisualDescendants().OfType<PinyinSeparateLinePresenter>()).OriginalText;
                var rectangle = original.TextLayout.HitTestTextPosition(marquee.Text!.Length - 1);
                var remaining = viewer.Offset.X - Math.Max(0, rectangle.Right - viewer.Viewport.Width);
                window.MouseWheel(point, new(remaining / (marquee.FontSize * 3), 0));
                PinyinTextRenderingTests.Pump(window);
                tailPoint = original.TranslatePoint(rectangle.Center, window)!.Value;
            }
            window.MouseMove(tailPoint);
            PinyinTextRenderingTests.Pump(window);
            Assert.Equal(marquee.Text!.Length - 1, text.HighlightedSourceStart);

            var offset = viewer.Offset.X;
            window.MouseMove(new(250, 160));
            await Task.Delay(120);
            PinyinTextRenderingTests.Pump(window);
            Assert.Equal(offset, viewer.Offset.X);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>普通纵向滚轮继续滚动消息容器，不会被注音横向滚动拦截。</summary>
    [AvaloniaTheory]
    [InlineData(PinyinLayoutMode.Ruby)]
    [InlineData(PinyinLayoutMode.SeparateLine)]
    public void VerticalWheelStillScrollsTheMessageContainer(PinyinLayoutMode layout)
    {
        var marquee = CreateMarquee(layout, false);
        var outer = new ScrollViewer
        {
            Content = new StackPanel { Children = { marquee, new Border { Height = 500 } } }
        };
        var window = CreateWindow(outer);
        try
        {
            window.Show();
            PinyinTextRenderingTests.Pump(window);
            var horizontal = marquee.GetVisualDescendants().OfType<ScrollViewer>().Single();
            var point = marquee.TranslatePoint(new(30, 35), window)!.Value;
            window.MouseMove(point);
            window.MouseWheel(point, new(0, -1));
            PinyinTextRenderingTests.Pump(window);
            Assert.True(outer.Offset.Y > 0);
            Assert.Equal(0, horizontal.Offset.X);
        }
        finally
        {
            window.Close();
        }
    }

    private static HoverMarqueeTextBlock CreateMarquee(PinyinLayoutMode layout, bool autoScroll)
    {
        var content = new string('长', 40);
        return new()
        {
            Text = content, Annotations = PinyinTextRenderingTests.Annotate(content), PinyinLayout = layout,
            AutoScroll = autoScroll, FontSize = 20, Height = 60,
            VerticalAlignment = VerticalAlignment.Top
        };
    }

    private static Window CreateWindow(Control content)
    {
        return new() { Width = 260, Height = 180, Content = new Border { Padding = new(10), Child = content } };
    }

    private static async Task WaitUntilAsync(Func<bool> condition, Window window)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(20);
            PinyinTextRenderingTests.Pump(window);
        }
        Assert.True(condition());
    }
}
