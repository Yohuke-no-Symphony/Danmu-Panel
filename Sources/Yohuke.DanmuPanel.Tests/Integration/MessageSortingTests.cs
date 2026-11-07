using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证各付费消息面板的阅读顺序、初始定位及历史窗口。</summary>
public sealed class MessageSortingTests
{
    /// <summary>默认未读从旧到新，已读从新到旧。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.SuperChat)]
    [InlineData(MessageKind.Gift)]
    [InlineData(MessageKind.Guard)]
    public async Task DefaultWindowsUseOppositeChronologicalOrders(MessageKind kind)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        await runtime.Messages.AppendBatchAsync(Enumerable.Range(0, 4).Select(index => Message(kind, index)).ToArray(), new());
        await runtime.Messages.MarkReadAsync("message-1", 1, 30);
        await runtime.Messages.MarkReadAsync("message-3", 1, 30);
        using var model = new FeedViewModel(runtime, kind);

        await model.SelectRoomAsync(new() { Id = "tab" });
        Assert.Equal(new[] { "message-0", "message-2" }, model.Items.Select(item => item.Record.Id));
        await model.SelectReadFilterAsync(MessageReadFilter.Read);
        Assert.Equal(new[] { "message-3", "message-1" }, model.Items.Select(item => item.Record.Id));
    }

    /// <summary>未读与已读标签首次显示时均从列表顶部开始。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.SuperChat, false)]
    [InlineData(MessageKind.Gift, false)]
    [InlineData(MessageKind.Guard, false)]
    [InlineData(MessageKind.SuperChat, true)]
    [InlineData(MessageKind.Gift, true)]
    [InlineData(MessageKind.Guard, true)]
    public async Task PaidFeedsStartAtTop(MessageKind kind, bool read)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        await runtime.Messages.AppendBatchAsync(Enumerable.Range(0, 60).Select(index => Message(kind, index)).ToArray(), new());
        if (read)
        {
            await runtime.Messages.MarkAllReadAsync("tab", kind);
        }
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, kind, errors.Add);
        var window = new Window { Width = 520, Height = 320, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "tab" });
            if (read)
            {
                await view.Model.SelectReadFilterAsync(MessageReadFilter.Read);
            }
            window.Show();
            await view.Model.WaitForPresentationAsync();
            Pump(window);

            var list = Assert.Single(view.GetVisualDescendants().OfType<ListBox>(), control => control.Name == "ScrollingMessages");
            var viewer = Assert.Single(list.GetVisualDescendants().OfType<ScrollViewer>(), control => control.TemplatedParent == list);
            Assert.True(viewer.ScrollBarMaximum.Y > 0);
            Assert.InRange(viewer.Offset.Y, 0, 1);
            Assert.NotNull(list.ContainerFromItem(view.Model.ScrollingItems[0]));
            var actions = Assert.IsType<StackPanel>(view.HeaderActionsTemplate.Build(view));
            var bottom = Assert.Single(actions.Children.OfType<Button>(), button => button.Name == "BackToLatestButton");
            await Assert.IsAssignableFrom<IAsyncRelayCommand>(bottom.Command).ExecuteAsync(null);
            Pump(window);
            Assert.InRange(Math.Abs(viewer.ScrollBarMaximum.Y - viewer.Offset.Y), 0, 1);
            await view.Model.SelectRoomAsync(new() { Id = "tab" });
            await view.Model.WaitForPresentationAsync();
            Pump(window);
            Assert.InRange(viewer.Offset.Y, 0, 1);
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>独立配置只改变对应面板，运行中保存后自动刷新当前标签。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.SuperChat)]
    [InlineData(MessageKind.Gift)]
    [InlineData(MessageKind.Guard)]
    public async Task SettingsApplyLiveOnlyToTheSelectedPanelOrder(MessageKind kind)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        var otherKind = kind == MessageKind.Gift ? MessageKind.SuperChat : MessageKind.Gift;
        await runtime.Messages.AppendBatchAsync(Enumerable.Range(0, 6).Select(index => Message(kind, index)).ToArray(), new());
        await runtime.Messages.AppendAsync(Message(otherKind, 10), new());
        for (var index = 1; index < 6; index += 2)
        {
            await runtime.Messages.MarkReadAsync("message-" + index, 1, 30);
        }
        using var model = new FeedViewModel(runtime, kind);
        using var other = new FeedViewModel(runtime, otherKind);
        await model.SelectRoomAsync(new() { Id = "tab" });
        await other.SelectRoomAsync(new() { Id = "tab" });
        var otherItem = Assert.Single(other.Items);
        var orders = new MessageReadSortSettings { Unread = MessageSortOrder.NewestFirst, Read = MessageSortOrder.OldestFirst };
        var settings = runtime.Configuration.Settings.MessageSorting;
        runtime.Configuration.Settings.MessageSorting = kind switch
        {
            MessageKind.SuperChat => settings with { SuperChat = orders },
            MessageKind.Gift => settings with { Gift = orders },
            MessageKind.Guard => settings with { Guard = orders },
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        await runtime.ApplySettingsAsync();
        for (var attempt = 0; attempt < 100 && model.SortOrder != MessageSortOrder.NewestFirst; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        await model.WaitForPresentationAsync();
        Assert.Equal(MessageSortOrder.NewestFirst, model.SortOrder);
        Assert.Equal(new[] { "message-4", "message-2", "message-0" }, model.Items.Select(item => item.Record.Id));
        Assert.Equal(MessageSortOrder.OldestFirst, other.SortOrder);
        Assert.Same(otherItem, Assert.Single(other.Items));
        await model.SelectReadFilterAsync(MessageReadFilter.Read);
        Assert.Equal(new[] { "message-1", "message-3", "message-5" }, model.Items.Select(item => item.Record.Id));
    }

    private static PanelRuntime CreateRuntime(MessageStoreTestDirectory directory)
    {
        var configuration = new PanelConfiguration();
        configuration.Settings.Translation.Enabled = false;
        configuration.Settings.MessageRetentionCount = 100;
        return new(new ConfigurationService(directory.Path), configuration);
    }

    private static MessageRecord Message(MessageKind kind, int index)
    {
        return new()
        {
            Id = "message-" + index, RoomTabId = "tab", RoomId = "123", Kind = kind,
            UserName = "viewer", Content = "消息 " + index, GiftName = "鲜花", GuardLevel = 3,
            UnitPrice = 30, TotalPrice = 30, Time = DateTimeOffset.FromUnixTimeSeconds(1800000000 + index)
        };
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
