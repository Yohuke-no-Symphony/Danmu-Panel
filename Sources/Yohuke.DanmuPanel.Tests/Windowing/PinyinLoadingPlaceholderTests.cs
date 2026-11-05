using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Integration;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Views;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Tests.Windowing;

/// <summary>验证拼音等待仅使用无文字小矩形，并保留真实原文与布局模式。</summary>
public sealed class PinyinLoadingPlaceholderTests
{
    /// <summary>矩形不创建文字和背景面板，合成动画不触发布局并在隐藏后停止。</summary>
    [AvaloniaFact]
    public async Task RectangleShimmersWithoutTextOrUiLayoutAndStopsWhenHidden()
    {
        var placeholder = new PinyinLoadingPlaceholder { FontSize = 10, Length = 4, Foreground = Brushes.Gray };
        var layout = new LayoutCountingDecorator { Child = placeholder };
        var window = new Window { Width = 240, Height = 100, Background = Brushes.White, Content = layout };
        try
        {
            window.Show();
            Pump(window);
            Assert.True(placeholder.IsShimmering);
            Assert.False(placeholder.IsHitTestVisible);
            Assert.Empty(placeholder.GetVisualDescendants().OfType<TextBlock>());
            Assert.Equal(40, placeholder.DesiredSize.Width);
            var first = Capture(window);
            var measures = layout.MeasurePasses;
            var arrangements = layout.ArrangePasses;

            await Task.Delay(650);
            Pump(window);

            Assert.False(first.SequenceEqual(Capture(window)));
            Assert.Equal(measures, layout.MeasurePasses);
            Assert.Equal(arrangements, layout.ArrangePasses);
            placeholder.IsVisible = false;
            Pump(window);
            Assert.False(placeholder.IsShimmering);
        }
        finally
        {
            window.Close();
        }
        Assert.False(placeholder.IsShimmering);
    }

    /// <summary>普通、紧凑及两种拼音布局均先保留原文，等待位置显示矩形并在完成后消失。</summary>
    [AvaloniaTheory]
    [InlineData(false, PinyinLayoutMode.Ruby)]
    [InlineData(true, PinyinLayoutMode.Ruby)]
    [InlineData(false, PinyinLayoutMode.SeparateLine)]
    [InlineData(true, PinyinLayoutMode.SeparateLine)]
    public async Task WaitingPinyinUsesSmallRectanglesWhileOriginalRemainsVisible(bool compact, PinyinLayoutMode pinyinLayout)
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = new PanelConfiguration
        {
            Settings = new()
            {
                ShowUserPinyin = true, ShowDanmuPinyin = true, PinyinLayout = pinyinLayout,
                CompactDanmuDisplay = compact, AutoScrollLongMessages = false
            }
        };
        var pinyin = new BlockingPinyinService();
        await using var runtime = new PanelRuntime(new(directory.Path), configuration, pinyin);
        var room = new RoomTabState { Id = "tab", RoomId = "123" };
        await runtime.TestMessages.AddAsync(room, new() { UserName = "小明", Content = "你好，拼音正在后台准备", Count = 1 });
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, MessageKind.Danmu, errors.Add);
        var window = new Window { Width = 640, Height = 240, Content = view };
        try
        {
            window.Show();
            await view.Model.SelectRoomAsync(room).WaitAsync(TimeSpan.FromSeconds(5));
            await pinyin.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Pump(window);
            var waiting = view.GetVisualDescendants().OfType<PinyinLoadingPlaceholder>().ToArray();
            Assert.Equal(2, waiting.Length);
            Assert.All(waiting, control =>
            {
                Assert.True(control.IsEffectivelyVisible);
                Assert.True(control.IsShimmering);
                Assert.False(control.IsHitTestVisible);
                Assert.Empty(control.GetVisualDescendants().OfType<TextBlock>());
                Assert.InRange(control.Bounds.Height, 1, 20);
                Assert.InRange(control.Bounds.Width, 1, 80);
            });
            var original = Assert.Single(view.GetVisualDescendants().OfType<EmoticonTextBlock>(),
                control => control.Classes.Contains("regularFeedContent") && control.Classes.Contains("originalText"));
            Assert.Equal("你好，拼音正在后台准备", original.ContentText);
            Assert.Equal(compact, view.GetVisualDescendants().OfType<HoverMarqueeTextBlock>()
                .Single(control => control.Classes.Contains("originalText")).IsEffectivelyVisible);
            var previewDirectory = Path.Combine(Path.GetTempPath(), "danmaku-panel-previews");
            Directory.CreateDirectory(previewDirectory);
            using (var preview = window.CaptureRenderedFrame())
            {
                preview!.Save(Path.Combine(previewDirectory,
                    $"pinyin-loading-{(compact ? "compact" : "normal")}-{pinyinLayout}.png"));
            }

            pinyin.Release.TrySetResult();
            await view.Model.WaitForPresentationAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Pump(window);

            Assert.All(waiting, control =>
            {
                Assert.False(control.IsVisible);
                Assert.False(control.IsShimmering);
            });
            Assert.NotEmpty(Assert.Single(view.Model.Items).ContentPinyin);
            Assert.Empty(errors);
        }
        finally
        {
            pinyin.Release.TrySetResult();
            view.Model.Dispose();
            window.Close();
        }
    }

    private static void Pump(Window window)
    {
        for (var pass = 0; pass < 4; pass++)
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
