using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Tests.Windowing;

/// <summary>验证历史骨架的虚拟化、非交互容器和动画生命周期。</summary>
public sealed class MessageFeedSkeletonTests
{
    /// <summary>骨架与真实弹幕共用列边界、徽章宽度、紧凑规则和字号资源。</summary>
    [AvaloniaTheory]
    [InlineData(false, 1.0)]
    [InlineData(true, 1.0)]
    [InlineData(false, 1.5)]
    [InlineData(true, 1.5)]
    public async Task SkeletonColumnsAndTypographyMatchRealMessageRows(bool compact, double scale)
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = new PanelConfiguration
        {
            Settings = new() { CompactDanmuDisplay = compact, MessageTextScale = scale }
        };
        await using var runtime = new PanelRuntime(new(directory.Path), configuration);
        using var model = new FeedViewModel(runtime, MessageKind.Danmu);
        var message = new MessageItemViewModel(new()
        {
            RoomTabId = "room", RoomId = "123", Kind = MessageKind.Danmu, UserName = "viewer",
            Content = "已经加载的弹幕正文", MedalName = "粉丝牌", MedalLevel = 20
        }, configuration.Settings, runtime.Pinyin);
        var rows = new ObservableCollection<FeedRowViewModel> { message };
        for (var index = 0; index < 3; index++)
        {
            rows.Add(new MessageLoadingPlaceholderViewModel());
        }
        var list = new MessageFeedListBox(() => new MessageFeedRow(MessageKind.Danmu)) { ItemsSource = rows };
        var surface = new MessageFeedSurface { DataContext = model };
        surface.Host.Content = list;
        surface.Resources["MessageFontSizeBody"] = 13 * scale;
        surface.Resources["MessageFontSizeCompact"] = 12 * scale;
        surface.Resources["MessageFontSizeMetadata"] = 11 * scale;
        surface.Resources["MessageMedalSlotWidth"] = 66 * scale;
        surface.Resources["MessageMedalLevelWidth"] = 20 * scale;
        var window = new Window { Width = 640, Height = 360, Content = surface };
        try
        {
            window.Show();
            Pump(window);
            var real = Assert.IsType<MessageFeedListBoxItem>(list.ContainerFromItem(message)).Row;
            var placeholder = Assert.IsType<MessageLoadingListBoxItem>(list.ContainerFromItem(rows[1])).Skeleton;
            var realLayout = Assert.Single(real.GetVisualDescendants().OfType<Grid>(), control => control.Classes.Contains("danmuFeedLayout"));
            var skeletonLayout = Assert.Single(placeholder.GetVisualDescendants().OfType<Grid>(), control => control.Classes.Contains("danmuFeedLayout"));
            Assert.Equal(realLayout.ColumnDefinitions[0].Width, skeletonLayout.ColumnDefinitions[0].Width);
            Assert.Equal(realLayout.ColumnDefinitions[1].Width, skeletonLayout.ColumnDefinitions[1].Width);
            Assert.Equal(realLayout.ColumnSpacing, skeletonLayout.ColumnSpacing);
            var realBody = Assert.Single(real.GetVisualDescendants().OfType<Panel>(), control => control.Classes.Contains("messageBody"));
            var skeletonBody = Assert.Single(placeholder.GetVisualDescendants().OfType<StackPanel>(), control => control.Classes.Contains("messageBody"));
            Assert.InRange(Math.Abs(realBody.TranslatePoint(default, list)!.Value.X -
                skeletonBody.TranslatePoint(default, list)!.Value.X), 0, 1);
            var realMedal = Assert.Single(real.GetVisualDescendants().OfType<Grid>(), control => control.Classes.Contains("danmuMedalSlot"));
            var skeletonMedal = Assert.Single(placeholder.GetVisualDescendants().OfType<Grid>(), control => control.Classes.Contains("danmuMedalSlot"));
            Assert.Equal(realMedal.Bounds.Width, skeletonMedal.Bounds.Width);
            var realName = Assert.Single(real.GetVisualDescendants().OfType<TextBlock>(), control => control.Classes.Contains("danmuFeedUser"));
            var skeletonName = Assert.Single(placeholder.GetVisualDescendants().OfType<TextBlock>(), control => control.Classes.Contains("danmuFeedUser"));
            Assert.Equal(realName.FontSize, skeletonName.FontSize);
            Assert.Equal(real.Padding, placeholder.Padding);
            Assert.Equal(compact ? 3 : 4, placeholder.PlaceholderRectangles.Count);
            var skeletonTime = Assert.Single(placeholder.GetVisualDescendants().OfType<TextBlock>(), control => control.Classes.Contains("danmuFeedMeta"));
            Assert.Equal(!compact, skeletonTime.IsVisible);

            var previewDirectory = Path.Combine(Path.GetTempPath(), "danmaku-panel-previews");
            Directory.CreateDirectory(previewDirectory);
            using var preview = window.CaptureRenderedFrame();
            preview!.Save(Path.Combine(previewDirectory,
                $"history-loading-{(compact ? "compact" : "normal")}-{scale * 100:0}.png"));

            configuration.Settings.ShowFanMedals = false;
            message.Update(message.Record, configuration.Settings);
            await model.RefreshAsync();
            Pump(window);

            Assert.False(realMedal.IsVisible);
            Assert.False(skeletonMedal.IsVisible);
            Assert.Equal(0, realLayout.ColumnDefinitions[0].ActualWidth);
            Assert.Equal(0, skeletonLayout.ColumnDefinitions[0].ActualWidth);
            Assert.Equal(compact ? 2 : 3, placeholder.PlaceholderRectangles.Count);
            Assert.InRange(Math.Abs(realBody.TranslatePoint(default, list)!.Value.X -
                skeletonBody.TranslatePoint(default, list)!.Value.X), 0, 1);

            configuration.Settings.ShowFanMedals = true;
            message.Update(message.Record, configuration.Settings);
            await model.RefreshAsync();
            Pump(window);

            Assert.True(realMedal.IsEffectivelyVisible);
            Assert.True(skeletonMedal.IsEffectivelyVisible);
            Assert.Equal(compact ? 3 : 4, placeholder.PlaceholderRectangles.Count);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>列表占位按单行虚拟化，与真实消息使用独立容器且不接受选择和焦点。</summary>
    [AvaloniaFact]
    public void PlaceholderRowsAreVirtualizedAndRecycleSeparatelyFromMessages()
    {
        var rows = new ObservableCollection<FeedRowViewModel>(Enumerable.Range(0, 100)
            .Select(_ => (FeedRowViewModel)new MessageLoadingPlaceholderViewModel()));
        var list = new MessageFeedListBox(() => new MessageFeedRow(MessageKind.Danmu)) { ItemsSource = rows };
        var window = new Window { Width = 640, Height = 320, Content = list };
        try
        {
            window.Show();
            Pump(window);

            var containers = list.GetRealizedContainers().ToArray();
            Assert.InRange(containers.Length, 1, 50);
            Assert.All(containers, container =>
            {
                var placeholder = Assert.IsType<MessageLoadingListBoxItem>(container);
                Assert.IsType<MessageLoadingPlaceholderViewModel>(placeholder.DataContext);
                Assert.False(placeholder.IsEnabled);
                Assert.False(placeholder.Focusable);
                Assert.False(placeholder.IsHitTestVisible);
                Assert.False(placeholder.Focus());
                Assert.Equal(1, placeholder.Skeleton.RowCount);
                Assert.Equal(4, placeholder.Skeleton.PlaceholderRectangles.Count);
                var presenter = Assert.Single(placeholder.Skeleton.GetVisualDescendants().OfType<ContentPresenter>(),
                    control => ReferenceEquals(control.TemplatedParent, placeholder.Skeleton));
                Assert.Equal(0, Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Background).Color.A);
                Assert.Equal(new Thickness(0), presenter.BorderThickness);
            });
            Assert.Equal(-1, list.SelectedIndex);

            var message = new MessageItemViewModel(new()
            {
                RoomTabId = "room", RoomId = "123", Kind = MessageKind.Danmu, UserName = "viewer", Content = "loaded"
            }, new PanelSettings(), new PinyinService());
            rows[0] = message;
            list.ScrollIntoView(message);
            Pump(window);
            var realContainer = Assert.IsType<MessageFeedListBoxItem>(list.ContainerFromItem(message));
            Assert.Same(message, realContainer.DataContext);
            Assert.Same(message, realContainer.Row.DataContext);
            Assert.True(realContainer.IsEnabled);
            var previewDirectory = Path.Combine(Path.GetTempPath(), "danmaku-panel-previews");
            Directory.CreateDirectory(previewDirectory);
            using (var preview = window.CaptureRenderedFrame())
            {
                preview!.Save(Path.Combine(previewDirectory, "history-loading-virtualization-stress.png"));
            }

            rows[0] = new MessageLoadingPlaceholderViewModel();
            list.ScrollIntoView(rows[0]);
            Pump(window);
            Assert.IsType<MessageLoadingListBoxItem>(list.ContainerFromItem(rows[0]));
            Assert.DoesNotContain(list.GetRealizedContainers(), container => ReferenceEquals(container, realContainer));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>显式三行预览使用真实消息的同一行徽章、姓名、时间和右侧正文布局。</summary>
    [AvaloniaFact]
    public void ExplicitThreeRowPreviewKeepsSeparateIdentityAndBodyBlocks()
    {
        var skeleton = new MessageFeedSkeleton { RowCount = 3, Foreground = Brushes.Gray, FontSize = 13 };
        var window = new Window { Width = 360, Height = 240, Content = skeleton };
        try
        {
            window.Show();
            Pump(window);
            Assert.False(skeleton.IsHitTestVisible);
            Assert.Equal(VerticalAlignment.Top, skeleton.VerticalAlignment);
            Assert.Equal(12, skeleton.PlaceholderRectangles.Count);
            for (var row = 0; row < 3; row++)
            {
                var medal = skeleton.PlaceholderRectangles[row * 4];
                var name = skeleton.PlaceholderRectangles[row * 4 + 1];
                var time = skeleton.PlaceholderRectangles[row * 4 + 2];
                var body = skeleton.PlaceholderRectangles[row * 4 + 3];
                Assert.InRange(Math.Abs(name.Center.Y - time.Center.Y), 0, 1);
                Assert.InRange(Math.Abs(name.Center.Y - body.Center.Y), 0, 1);
                Assert.True(name.Left > medal.Right);
                Assert.True(time.Left > name.Right);
                Assert.True(body.Left > time.Right);
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>扫光由合成线程推进，不逐帧修改 UI 属性或触发布局。</summary>
    [AvaloniaFact]
    public async Task CompositionShimmerAdvancesWithoutUiChangesOrLayout()
    {
        var skeleton = new MessageFeedSkeleton { Foreground = Brushes.Black, FontSize = 16 };
        var layout = new LayoutCountingDecorator { Child = skeleton };
        var window = new Window { Width = 360, Height = 220, Background = Brushes.White, Content = layout };
        try
        {
            window.Show();
            Pump(window);
            Assert.True(skeleton.IsShimmering);
            var firstFrame = Capture(window);
            var propertyChanges = 0;
            var measurePasses = layout.MeasurePasses;
            var arrangePasses = layout.ArrangePasses;
            skeleton.PropertyChanged += (_, _) => propertyChanges++;

            await Task.Delay(650);
            Pump(window);

            Assert.False(firstFrame.SequenceEqual(Capture(window)));
            Assert.Equal(0, propertyChanges);
            Assert.Equal(measurePasses, layout.MeasurePasses);
            Assert.Equal(arrangePasses, layout.ArrangePasses);
        }
        finally
        {
            window.Close();
        }
        Assert.False(skeleton.IsShimmering);
    }

    /// <summary>隐藏、祖先隐藏、移出视口或移除后停止动画，再次显示后恢复。</summary>
    [AvaloniaTheory]
    [InlineData("hidden")]
    [InlineData("ancestor")]
    [InlineData("detach")]
    [InlineData("viewport")]
    public void ShimmerStopsOutsideTheVisibleTreeAndResumes(string change)
    {
        var skeleton = new MessageFeedSkeleton { Foreground = Brushes.Gray };
        var parent = new Border { Child = skeleton };
        var viewer = new ScrollViewer
        {
            Content = new StackPanel { Children = { parent, new Border { Height = 600 } } }
        };
        var window = new Window { Width = 300, Height = 200, Content = viewer };
        try
        {
            Assert.False(skeleton.IsShimmering);
            window.Show();
            Pump(window);
            Assert.True(skeleton.IsShimmering);

            switch (change)
            {
                case "hidden":
                    skeleton.IsVisible = false;
                    break;
                case "ancestor":
                    parent.IsVisible = false;
                    break;
                case "detach":
                    parent.Child = null;
                    break;
                case "viewport":
                    viewer.Offset = new(0, 350);
                    break;
            }
            Pump(window);
            Assert.False(skeleton.IsShimmering);

            skeleton.IsVisible = true;
            parent.IsVisible = true;
            parent.Child = skeleton;
            viewer.Offset = default;
            Pump(window);
            Assert.True(skeleton.IsShimmering);
        }
        finally
        {
            window.Close();
        }
        Assert.False(skeleton.IsShimmering);
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
