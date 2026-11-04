using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Xunit;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Tests.Windowing;

/// <summary>验证扫光不会修改文字布局，并随可见性和视口启停。</summary>
public sealed class ShimmerTextBlockTests
{
    /// <summary>多条等待文字实际推进扫光时，不逐帧修改 UI 对象或重新排版。</summary>
    [AvaloniaFact]
    public async Task MultiplePlaceholdersAnimateWithoutUiPropertyChangesOrLayout()
    {
        var texts = Enumerable.Range(0, 24).Select(_ => new ShimmerTextBlock
        {
            Text = "翻译中 Translating", FontSize = 20, Foreground = Brushes.Black,
            HorizontalAlignment = HorizontalAlignment.Left
        }).ToArray();
        var panel = new StackPanel();
        foreach (var text in texts)
        {
            panel.Children.Add(text);
        }
        var layout = new LayoutCountingDecorator { Child = panel };
        var window = new Window { Width = 300, Height = 900, Background = Brushes.White, Content = layout };
        try
        {
            window.Show();
            Pump(window);
            Assert.All(texts, text => Assert.True(text.IsShimmering));
            var firstFrame = Capture(window);
            var changes = 0;
            var measurePasses = layout.MeasurePasses;
            var arrangePasses = layout.ArrangePasses;
            foreach (var text in texts)
            {
                text.PropertyChanged += (_, _) => changes++;
                if (text.OpacityMask is AvaloniaObject mask)
                {
                    mask.PropertyChanged += (_, _) => changes++;
                }
            }

            await Task.Delay(650);
            Pump(window);

            Assert.False(firstFrame.SequenceEqual(Capture(window)), "The rendered shimmer must advance.");
            Assert.Equal(0, changes);
            Assert.Equal(measurePasses, layout.MeasurePasses);
            Assert.Equal(arrangePasses, layout.ArrangePasses);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>文字保持原位，扫光持续移动；隐藏或移除后停止，恢复显示后重新播放。</summary>
    [AvaloniaTheory]
    [InlineData("hidden")]
    [InlineData("ancestor")]
    [InlineData("detach")]
    [InlineData("viewport")]
    public async Task ShimmerMovesWithoutLayoutChangesAndStopsOutsideVisibleTree(string change)
    {
        var text = new ShimmerTextBlock { Text = "翻译中", HorizontalAlignment = HorizontalAlignment.Left };
        var parent = new Border { Child = text, Height = 40 };
        var viewer = new ScrollViewer
        {
            Content = new StackPanel { Children = { parent, new Border { Height = 600 } } }
        };
        var window = new Window { Width = 240, Height = 180, Content = viewer };
        try
        {
            Assert.False(text.IsShimmering);
            window.Show();
            Pump(window);
            Assert.True(text.IsShimmering);
            var firstFrame = Capture(window);
            var bounds = text.Bounds;
            await Task.Delay(650);
            Pump(window);
            Assert.False(firstFrame.SequenceEqual(Capture(window)), "The rendered shimmer must advance.");
            Assert.Equal(bounds, text.Bounds);
            Assert.Equal("翻译中", text.Text);

            switch (change)
            {
                case "hidden":
                    text.IsVisible = false;
                    break;
                case "ancestor":
                    parent.IsVisible = false;
                    break;
                case "detach":
                    parent.Child = null;
                    break;
                case "viewport":
                    viewer.Offset = new(0, 300);
                    break;
            }
            Pump(window);
            Assert.False(text.IsShimmering);
            var stoppedFrame = Capture(window);
            await Task.Delay(100);
            Pump(window);
            Assert.Equal(stoppedFrame, Capture(window));

            text.IsVisible = true;
            parent.IsVisible = true;
            parent.Child = text;
            viewer.Offset = default;
            Pump(window);
            Assert.True(text.IsShimmering);
        }
        finally
        {
            window.Close();
        }
        Assert.False(text.IsShimmering);
    }

    private static void Pump(Window window)
    {
        for (var frame = 0; frame < 4; frame++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        }
    }

    private static byte[] Capture(Window window)
    {
        using var frame = window.CaptureRenderedFrame();
        using var stream = new MemoryStream();
        frame!.Save(stream);
        return stream.ToArray();
    }
}
