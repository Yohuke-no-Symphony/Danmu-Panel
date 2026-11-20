using System.Collections.Specialized;
using Avalonia.Headless.XUnit;
using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.ViewModels;

namespace Yohuke.DanmuPanel.Tests.Integration;

public sealed class LiveDanmuWindowTests
{
    [AvaloniaFact]
    public async Task CommittedDanmuAppendsWithoutQueryingOrRebindingTheRetainedRows()
    {
        using var directory = new MessageStoreTestDirectory();
        var settings = new PanelSettings { MessageRetentionCount = 3 };
        settings.Translation.Enabled = false;
        await using var runtime = new PanelRuntime(new(directory.Path), new PanelConfiguration { Settings = settings });
        using var feed = new FeedViewModel(runtime, MessageKind.Danmu);
        for (var index = 0; index < 7; index++)
        {
            await runtime.Messages.AppendAsync(Message(index), settings.Reading);
        }
        await feed.SelectRoomAsync(new() { Id = "room" });
        await feed.WaitForPresentationAsync();
        Assert.True(feed.HasOlderHistory);
        feed.IsReadingHistory = true;
        var original = feed.ScrollingItems.ToArray();
        var changes = new List<NotifyCollectionChangedEventArgs>();
        feed.ScrollingRows.CollectionChanged += (_, args) => changes.Add(args);
        var payloadRows = runtime.Messages.PayloadRowsRead;

        var commit = await runtime.Messages.AppendAsync(Message(7), settings.Reading);
        runtime.PublishCommittedMessage(Assert.IsType<MessageRecord>(commit.Record));
        await WaitForLastMessageAsync(feed, "danmu-7");
        await feed.WaitForPresentationAsync();

        Assert.Equal(payloadRows, runtime.Messages.PayloadRowsRead);
        Assert.Equal(new[] { NotifyCollectionChangedAction.Remove, NotifyCollectionChangedAction.Add },
            changes.Select(change => change.Action));
        Assert.Same(original[1], feed.ScrollingItems[0]);
        Assert.Same(original[2], feed.ScrollingItems[1]);
        Assert.Equal(3, feed.ScrollingItems.Count);

        await feed.LoadMoreAsync();
        Assert.False(feed.HasOlderHistory);
        Assert.Equal(8, feed.ScrollingItems.Count);
        Assert.Equal("danmu-0", feed.ScrollingItems[0].Record.Id);
        Assert.Same(original[1], feed.ScrollingItems[5]);
    }

    [AvaloniaFact]
    public async Task LiveDanmuAppliesVisibilityFiltersWithoutReadingTheDatabaseAgain()
    {
        using var directory = new MessageStoreTestDirectory();
        var settings = new PanelSettings
        {
            MessageRetentionCount = 3,
            HideEmoticonDanmu = true,
            DanmuRegexFilter = new() { Rules = [new() { Pattern = "^隐藏", BlockDisplay = true }] }
        };
        settings.Translation.Enabled = false;
        await using var runtime = new PanelRuntime(new(directory.Path), new PanelConfiguration { Settings = settings });
        using var feed = new FeedViewModel(runtime, MessageKind.Danmu);
        for (var index = 0; index < 3; index++)
        {
            await runtime.Messages.AppendAsync(Message(index), settings.Reading);
        }
        await feed.SelectRoomAsync(new() { Id = "room" });
        Assert.False(feed.HasOlderHistory);
        var original = feed.ScrollingItems.ToArray();
        var evaluations = runtime.Messages.FeedVisibilityEvaluations;

        var messages = new[]
        {
            Message(3) with { Content = "隐藏这行" },
            Message(4) with { Content = "🎵" },
            Message(5)
        };
        foreach (var message in messages)
        {
            var commit = await runtime.Messages.AppendAsync(message, settings.Reading);
            runtime.PublishCommittedMessage(Assert.IsType<MessageRecord>(commit.Record));
        }
        await WaitForLastMessageAsync(feed, "danmu-5");

        Assert.True(feed.HasOlderHistory);
        Assert.Equal(evaluations, runtime.Messages.FeedVisibilityEvaluations);
        Assert.Equal(["danmu-1", "danmu-2", "danmu-5"], feed.ScrollingItems.Select(item => item.Record.Id));
        Assert.Same(original[1], feed.ScrollingItems[0]);
        Assert.Same(original[2], feed.ScrollingItems[1]);
    }

    private static async Task WaitForLastMessageAsync(FeedViewModel feed, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (feed.ScrollingItems[^1].Record.Id != id)
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private static MessageRecord Message(int index)
    {
        return new()
        {
            Id = $"danmu-{index}",
            RoomTabId = "room",
            Kind = MessageKind.Danmu,
            Content = $"弹幕 {index}",
            Time = DateTimeOffset.FromUnixTimeMilliseconds(1800000000000 + index)
        };
    }
}
