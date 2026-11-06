using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.ViewModels;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证礼物门槛应用到可见列表，优先排序与消息阅读状态保持独立。</summary>
public sealed class GiftPriorityFeedTests
{
    /// <summary>保存门槛后自动重排已有行，达到门槛的未读礼物优先于更早的不达标礼物。</summary>
    [AvaloniaFact]
    public async Task SavedThresholdAutomaticallyReordersVisibleGifts()
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = new PanelConfiguration();
        configuration.Settings.Translation.Enabled = false;
        configuration.Settings.Reading = new() { GiftAutoPinMinimum = 100 };
        await using var runtime = new PanelRuntime(new(directory.Path), configuration);
        var smaller = Gift("smaller", 0, 50);
        var target = Gift("target", 1, 150);
        var read = Gift("read", 2, 300);
        await runtime.Messages.AppendBatchAsync([smaller, target, read], configuration.Settings.Reading);
        await runtime.Messages.MarkReadAsync(read.Id, read.Quantity, read.TotalPrice);
        using var model = new FeedViewModel(runtime, MessageKind.Gift);
        var errors = new List<Exception>();
        model.Failed += errors.Add;
        await model.SelectRoomAsync(new() { Id = "tab", RoomId = "123" });
        var targetItem = model.Items.Single(item => item.Record.Id == target.Id);
        var smallerItem = model.Items.Single(item => item.Record.Id == smaller.Id);
        Assert.Equal(new[] { "target", "smaller", "read" }, model.ScrollingItems.Select(item => item.Record.Id));

        configuration.Settings.Reading = new() { GiftAutoPinMinimum = 200 };
        await runtime.ApplySettingsAsync();
        await WaitUntilAsync(() => !targetItem.Record.IsPriority);

        Assert.Equal(new[] { "smaller", "target", "read" }, model.ScrollingItems.Select(item => item.Record.Id));
        Assert.True(targetItem.ShowUnreadDot);
        Assert.True(smallerItem.ShowUnreadDot);
        Assert.Equal(2, model.UnreadCount);

        configuration.Settings.Reading = new() { GiftAutoPinMinimum = 50 };
        await runtime.ApplySettingsAsync();
        await WaitUntilAsync(() => smallerItem.Record.IsPriority);
        Assert.True(targetItem.Record.IsPriority);

        configuration.Settings.Reading = new() { GiftAutoPinMinimum = 150 };
        await runtime.ApplySettingsAsync();
        await WaitUntilAsync(() => !smallerItem.Record.IsPriority);

        Assert.Equal(new[] { "target", "smaller", "read" }, model.ScrollingItems.Select(item => item.Record.Id));
        Assert.Same(targetItem, model.ScrollingItems[0]);
        Assert.Same(smallerItem, model.ScrollingItems[1]);
        Assert.True(targetItem.Record.IsPriority);
        Assert.Equal(2, model.UnreadCount);
        Assert.Empty(model.PinnedItems);
        var confirmed = model.Items.Single(item => item.Record.Id == read.Id);
        Assert.True(confirmed.Record.IsRead);
        Assert.False(confirmed.Record.IsPriority);
        Assert.Empty(errors);
    }

    /// <summary>同一礼物连击累计到门槛时复用原行，并进入列表头的未读优先组。</summary>
    [AvaloniaFact]
    public async Task ComboReachingThresholdMovesExistingRowAheadOfOlderGift()
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = new PanelConfiguration();
        configuration.Settings.Translation.Enabled = false;
        configuration.Settings.Reading = new() { GiftAutoPinMinimum = 8 };
        await using var runtime = new PanelRuntime(new(directory.Path), configuration);
        var smaller = Gift("smaller", 0, 1);
        var combo = Gift("combo", 1, 4) with { ComboId = "combo-id", BatchComboCount = 1, MessageId = "first" };
        await runtime.Messages.AppendBatchAsync([smaller, combo], configuration.Settings.Reading);
        using var model = new FeedViewModel(runtime, MessageKind.Gift);
        await model.SelectRoomAsync(new() { Id = "tab", RoomId = "123" });
        var comboItem = model.ScrollingItems[1];
        Assert.Equal(combo.Id, comboItem.Record.Id);
        Assert.False(comboItem.Record.IsPriority);

        await runtime.Messages.AppendAsync(combo with
        {
            Id = "second", MessageId = "second", BatchComboCount = 2
        }, configuration.Settings.Reading);
        await model.RefreshAsync();

        Assert.Same(comboItem, model.ScrollingItems[0]);
        Assert.Equal(smaller.Id, model.ScrollingItems[1].Record.Id);
        Assert.Equal(2, comboItem.Record.Quantity);
        Assert.Equal(8m, comboItem.Record.TotalPrice);
        Assert.True(comboItem.Record.IsPriority);
        Assert.True(comboItem.ShowUnreadDot);
        Assert.Equal(2, model.UnreadCount);
        Assert.Empty(model.PinnedItems);
    }

    private static MessageRecord Gift(string id, int sequence, decimal price)
    {
        return new()
        {
            Id = id, RoomTabId = "tab", RoomId = "123", Kind = MessageKind.Gift,
            UserUid = 42, UserName = "viewer", GiftId = "flower", GiftName = "鲜花",
            UnitPrice = price, TotalPrice = price, Time = DateTimeOffset.FromUnixTimeSeconds(1800000000 + sequence)
        };
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition())
            {
                return;
            }
            await Task.Delay(10);
        }
        Assert.Fail("The saved gift threshold was not applied to the visible feed.");
    }
}
