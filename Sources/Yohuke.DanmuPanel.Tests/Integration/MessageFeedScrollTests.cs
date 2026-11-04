using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证四类真实消息列表的底部追加、历史保位及阅读状态更新。</summary>
public sealed class MessageFeedScrollTests
{
    /// <summary>四类消息在底部跟随，离开底部后追加和已读更新都保留当前阅读位置。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.Danmu)]
    [InlineData(MessageKind.SuperChat)]
    [InlineData(MessageKind.Gift)]
    [InlineData(MessageKind.Guard)]
    public async Task AppendingMessagesPreservesHistoryAndFollowsOnlyAtBottom(MessageKind kind)
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = new PanelConfiguration();
        configuration.Settings.Translation.Enabled = false;
        await using var runtime = new PanelRuntime(new ConfigurationService(directory.Path), configuration);
        var time = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        for (var index = 0; index < 140; index++)
        {
            await runtime.Messages.AppendAsync(Message(kind, time.AddSeconds(index)), new());
        }
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, kind, errors.Add);
        var window = new Window { Width = 500, Height = 360, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "tab" });
            window.Show();
            Pump(window);
            var list = Assert.Single(view.GetVisualDescendants().OfType<ListBox>(), control => control.Name == "ScrollingMessages");
            var viewer = Assert.Single(list.GetVisualDescendants().OfType<ScrollViewer>(), control => control.TemplatedParent == list);
            AssertAtBottom(viewer);
            Assert.False(view.Model.IsReadingHistory);
            Assert.Equal(view.Model.ScrollingItems.OrderBy(item => item.Record.Time), view.Model.ScrollingItems);
            var latest = Message(kind, time.AddSeconds(140));
            await runtime.Messages.AppendAsync(latest, new());
            await view.Model.RefreshAsync();
            Pump(window);
            Assert.Equal(latest.Id, view.Model.ScrollingItems[^1].Record.Id);
            AssertAtBottom(viewer);

            viewer.Offset = new(0, viewer.ScrollBarMaximum.Y / 2);
            Pump(window);
            Assert.True(view.Model.IsReadingHistory);
            var anchor = Assert.IsAssignableFrom<Control>(viewer.CurrentAnchor);
            var item = Assert.IsType<MessageItemViewModel>(list.ItemFromContainer(anchor));
            var before = anchor.TranslatePoint(default, viewer)!.Value.Y;
            var oldest = view.Model.ScrollingItems[0];
            var changes = new List<NotifyCollectionChangedEventArgs>();
            view.Model.ScrollingItems.CollectionChanged += (_, args) => changes.Add(args);
            for (var index = 141; index < 145; index++)
            {
                await runtime.Messages.AppendAsync(Message(kind, time.AddSeconds(index)), new());
            }
            await view.Model.RefreshAsync();
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
            window.UpdateLayout();
            var after = Assert.IsAssignableFrom<Control>(list.ContainerFromItem(item));
            Assert.InRange(Math.Abs(after.TranslatePoint(default, viewer)!.Value.Y - before), 0, 1);
            Assert.Same(oldest, view.Model.ScrollingItems[0]);
            Assert.All(changes, change => Assert.Equal(NotifyCollectionChangedAction.Add, change.Action));
            Assert.True(view.Model.IsReadingHistory);

            if (kind != MessageKind.Danmu)
            {
                await runtime.Messages.MarkReadAsync(oldest.Record.Id, oldest.Record.Quantity, oldest.Record.TotalPrice);
                await view.Model.RefreshAsync();
                Pump(window);
                Assert.Same(oldest, view.Model.ScrollingItems[^1]);
                Assert.True(oldest.Record.IsRead);
                after = Assert.IsAssignableFrom<Control>(list.ContainerFromItem(item));
                Assert.InRange(Math.Abs(after.TranslatePoint(default, viewer)!.Value.Y - before), 0, 1);
            }

            var actions = view.HeaderActionsTemplate.Build(view);
            var latestButton = actions as Button ?? Assert.IsType<StackPanel>(actions).Children.OfType<Button>().Single(button => button.Name != "MarkAllReadButton");
            await Assert.IsAssignableFrom<IAsyncRelayCommand>(latestButton.Command).ExecuteAsync(null);
            Pump(window);
            AssertAtBottom(viewer);
            Assert.False(view.Model.IsReadingHistory);
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
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }
}
