using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Views;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证快速刷屏和滚动返回底部交错时的跟随状态。</summary>
public sealed class MessageFeedRapidScrollTests
{
    /// <summary>快速刷屏产生整页裁剪时，通过标题按钮或滚动返回底部后持续跟随。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReturningToBottomFollowsRepeatedRefreshesAndPageTrimming(bool useButton)
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = new PanelConfiguration();
        configuration.Settings.MessageRetentionCount = 100;
        configuration.Settings.Translation.Enabled = false;
        configuration.Settings.CompactDanmuWrap = true;
        await using var runtime = new PanelRuntime(new ConfigurationService(directory.Path), configuration);
        var time = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        for (var index = 0; index < 160; index++)
        {
            await runtime.Messages.AppendAsync(Message(MessageKind.Danmu, time.AddSeconds(index)) with
            {
                Content = string.Concat(Enumerable.Repeat($"message {index} ", index % 7 + 1))
            }, new());
        }
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, MessageKind.Danmu, errors.Add);
        var window = new Window { Width = 380, Height = 300, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "tab" });
            window.Show();
            Pump(window);
            var list = Assert.Single(view.GetVisualDescendants().OfType<ListBox>(), control => control.Name == "ScrollingMessages");
            var viewer = Assert.Single(list.GetVisualDescendants().OfType<ScrollViewer>(), control => control.TemplatedParent == list);
            var presenter = Assert.Single(viewer.GetVisualDescendants().OfType<ScrollContentPresenter>(),
                presenter => ReferenceEquals(presenter.TemplatedParent, viewer));
            presenter.RaiseEvent(new ScrollGestureEventArgs(ScrollGestureEventArgs.GetNextFreeId(),
                new(0, viewer.ScrollBarMaximum.Y / 2 - viewer.Offset.Y)));
            Pump(window);
            Assert.True(viewer.Offset.Y > 0 && viewer.Offset.Y < viewer.ScrollBarMaximum.Y);
            Assert.True(view.Model.IsReadingHistory);
            if (useButton)
            {
                var button = Assert.IsType<StackPanel>(view.HeaderActionsTemplate.Build(view)).Children.OfType<Button>()
                    .Single(button => button.Name == "BackToLatestButton");
                await Assert.IsAssignableFrom<IAsyncRelayCommand>(button.Command).ExecuteAsync(null);
            }
            else
            {
                viewer.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.End });
                viewer.ScrollToEnd();
            }
            Pump(window);
            Assert.False(view.Model.IsReadingHistory);
            var sequence = 160;
            for (var batch = 0; batch < 12; batch++)
            {
                var count = new[] { 20, 100, 150 }[batch % 3];
                for (var index = 0; index < count; index++, sequence++)
                {
                    await runtime.Messages.AppendAsync(Message(MessageKind.Danmu, time.AddSeconds(sequence)) with
                    {
                        Content = string.Concat(Enumerable.Repeat($"batch {batch} item {index} ", index % 7 + 1))
                    }, new());
                }
                await view.Model.RefreshAsync();
                window.UpdateLayout();
                Pump(window);

                Assert.False(view.Model.IsReadingHistory, $"Batch {batch}: offset={viewer.Offset.Y}, maximum={viewer.ScrollBarMaximum.Y}");
                AssertAtBottom(viewer);
                Assert.NotNull(list.ContainerFromItem(view.Model.ScrollingItems[^1]));
                Assert.Equal(100, view.Model.ScrollingItems.Count);
            }
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    private static MessageRecord Message(MessageKind kind, DateTimeOffset time)
    {
        return new()
        {
            RoomTabId = "tab", RoomId = "123", Kind = kind, Time = time,
            UserName = "viewer", Content = "message", GiftName = "gift", TotalPrice = 100, GuardLevel = 3
        };
    }

    private static void AssertAtBottom(ScrollViewer viewer)
    {
        Assert.InRange(Math.Abs(viewer.ScrollBarMaximum.Y - viewer.Offset.Y), 0, 1);
    }

    private static void Pump(Window window)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }
}
