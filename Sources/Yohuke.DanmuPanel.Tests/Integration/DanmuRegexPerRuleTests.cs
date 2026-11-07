using Avalonia.Headless.XUnit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.ViewModels;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证混合规则在历史显示、置顶和逐条补翻中的独立行为。</summary>
public sealed class DanmuRegexPerRuleTests
{
    /// <summary>修改一条规则只改变其命中消息，其他规则及 SC 的行为继续保留。</summary>
    [AvaloniaFact]
    public async Task IndividualRulesControlVisibilityAndHistoryTranslationIndependently()
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = new PanelConfiguration
        {
            Settings = new()
            {
                Translation = new() { Enabled = true, Model = "test" },
                DanmuRegexFilter = new()
                {
                    Rules =
                    [
                        new() { Pattern = "^广告", BlockDisplay = true },
                        new() { Pattern = "♪", BlockTranslation = true },
                        new() { Pattern = "^停用" }
                    ]
                }
            }
        };
        await using var runtime = new PanelRuntime(new(directory.Path), configuration);
        var hidden = Message("广告内容");
        var lyrics = Message("歌词♪");
        var disabled = Message("停用内容");
        var overlap = Message("广告歌词♪");
        var ordinary = Message("ordinary");
        var superChat = Message("广告歌词♪") with { Kind = MessageKind.SuperChat };
        await runtime.Messages.AppendBatchAsync([hidden, lyrics, disabled, overlap, ordinary, superChat], new());
        await runtime.Messages.TogglePinAsync(lyrics.Id);
        using var danmu = new FeedViewModel(runtime, MessageKind.Danmu);
        using var paid = new FeedViewModel(runtime, MessageKind.SuperChat);

        await danmu.SelectRoomAsync(new() { Id = "tab" });
        await paid.SelectRoomAsync(new() { Id = "tab" });
        await danmu.WaitForPresentationAsync();
        await paid.WaitForPresentationAsync();

        Assert.Equal(3, danmu.Items.Count);
        Assert.DoesNotContain(danmu.Items, item => item.Record.Id == hidden.Id || item.Record.Id == overlap.Id);
        var lyricItem = danmu.Items.Single(item => item.Record.Id == lyrics.Id);
        Assert.Same(lyricItem, Assert.Single(danmu.PinnedItems));
        Assert.True(lyricItem.ShowOriginal);
        Assert.False(lyricItem.IsTranslating);
        Assert.True(danmu.Items.Single(item => item.Record.Id == disabled.Id).IsTranslating);
        Assert.True(Assert.Single(paid.Items).IsTranslating);
        Assert.Equal(new[] { disabled.Id, ordinary.Id, superChat.Id }.Order(StringComparer.Ordinal),
            (await runtime.Messages.GetPendingAsync(10)).Select(job => job.MessageId).Order(StringComparer.Ordinal));

        configuration.Settings.DanmuRegexFilter.Rules[1].BlockTranslation = false;
        await runtime.ApplySettingsAsync();
        await danmu.RefreshAsync();
        await danmu.WaitForPresentationAsync();

        Assert.Same(lyricItem, danmu.Items.Single(item => item.Record.Id == lyrics.Id));
        Assert.True(lyricItem.IsTranslating);
        Assert.Equal(3, danmu.Items.Count);
        Assert.Equal(4, (await runtime.Messages.GetPendingAsync(10)).Count);

        configuration.Settings.DanmuRegexFilter.Rules[0].BlockDisplay = false;
        await runtime.ApplySettingsAsync();
        await danmu.RefreshAsync();
        await danmu.WaitForPresentationAsync();

        Assert.Equal(5, danmu.Items.Count);
        Assert.Equal(6, (await runtime.Messages.GetPendingAsync(10)).Count);
        Assert.True((await runtime.Messages.GetByIdAsync(lyrics.Id))!.IsPinned);
    }

    private static MessageRecord Message(string content)
    {
        return new() { RoomTabId = "tab", Content = content };
    }
}
