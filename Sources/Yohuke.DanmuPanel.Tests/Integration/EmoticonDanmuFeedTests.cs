using Avalonia.Headless.XUnit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.ViewModels;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证隐藏纯表情后的消息分页、置顶和设置切换。</summary>
public sealed class EmoticonDanmuFeedTests
{
    /// <summary>过滤只影响普通弹幕，切回显示时恢复消息和置顶状态。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.Danmu)]
    [InlineData(MessageKind.SuperChat)]
    [InlineData(MessageKind.Gift)]
    [InlineData(MessageKind.Guard)]
    public async Task FilterOnlyHidesDanmuAndRetainsStoredMessages(MessageKind kind)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new PanelConfiguration());
        var emoticon = Message("[笑] 🙂", 0, kind);
        var text = Message("你好[笑]", 1, kind);
        var punctuation = Message("!!!", 2, kind);
        foreach (var record in new[] { emoticon, text, punctuation })
        {
            await runtime.Messages.AppendAsync(record, new());
        }
        if (kind == MessageKind.Danmu)
        {
            await runtime.Messages.TogglePinAsync(emoticon.Id);
        }
        using var model = new FeedViewModel(runtime, kind);
        await model.SelectRoomAsync(new() { Id = "tab", RoomId = "123" });
        var textItem = model.Items.Single(item => item.Record.Id == text.Id);
        Assert.Equal(3, model.Items.Count);

        runtime.Configuration.Settings.HideEmoticonDanmu = true;
        await runtime.ApplySettingsAsync();
        await model.RefreshAsync();

        Assert.Equal(kind == MessageKind.Danmu ? 2 : 3, model.Items.Count);
        Assert.Same(textItem, model.Items.Single(item => item.Record.Id == text.Id));
        Assert.Contains(model.Items, item => item.Record.Id == punctuation.Id);
        Assert.NotNull(await runtime.Messages.GetByIdAsync(emoticon.Id));
        if (kind == MessageKind.Danmu)
        {
            Assert.Empty(model.PinnedItems);
        }
        else
        {
            Assert.Equal(3, model.UnreadCount);
        }

        runtime.Configuration.Settings.HideEmoticonDanmu = false;
        await runtime.ApplySettingsAsync();
        await model.RefreshAsync();

        Assert.Equal(3, model.Items.Count);
        if (kind == MessageKind.Danmu)
        {
            Assert.Equal(emoticon.Id, Assert.Single(model.PinnedItems).Record.Id);
        }
    }

    /// <summary>连续表情不会挤占可见页数，历史分页继续加载更早的文字。</summary>
    [AvaloniaFact]
    public async Task HiddenEmoticonsDoNotConsumeVisiblePageSlots()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new PanelConfiguration
        {
            Settings = new() { HideEmoticonDanmu = true }
        });
        for (var index = 0; index < 120; index++)
        {
            await runtime.Messages.AppendAsync(Message($"文字{index}", index), new());
            await runtime.Messages.AppendAsync(Message("[笑]", index), new());
        }
        for (var index = 120; index < 230; index++)
        {
            await runtime.Messages.AppendAsync(Message("😀", index), new());
        }
        var pinnedEmoticon = Message("[笑]", 230);
        await runtime.Messages.AppendAsync(pinnedEmoticon, new());
        await runtime.Messages.TogglePinAsync(pinnedEmoticon.Id);
        using var model = new FeedViewModel(runtime, MessageKind.Danmu);
        await model.SelectRoomAsync(new() { Id = "tab", RoomId = "123" });

        Assert.Equal(100, model.ScrollingItems.Count);
        Assert.Equal("文字20", model.ScrollingItems[0].Record.Content);
        Assert.Equal("文字119", model.ScrollingItems[^1].Record.Content);

        model.IsReadingHistory = true;
        await model.RefreshAsync();

        Assert.Equal(100, model.ScrollingItems.Count);
        Assert.Empty(model.PinnedItems);

        await model.LoadMoreAsync();

        Assert.Equal(120, model.ScrollingItems.Count);
        Assert.Equal("文字0", model.ScrollingItems[0].Record.Content);
        Assert.Equal("文字119", model.ScrollingItems[^1].Record.Content);
    }

    private static MessageRecord Message(string content, int seconds, MessageKind kind = MessageKind.Danmu)
    {
        return new()
        {
            RoomTabId = "tab", RoomId = "123", UserName = "viewer", Kind = kind,
            Content = content, Time = DateTimeOffset.UtcNow.Date.AddSeconds(seconds),
            GiftName = content,
            Emoticons = [new() { Text = "[笑]" }]
        };
    }
}
