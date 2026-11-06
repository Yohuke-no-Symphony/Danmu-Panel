using Avalonia.Headless.XUnit;
using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Messages;
using Yohuke.DanmuPanel.Tests.Messages;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证运行时启动时按已保存的礼物门槛校正历史优先状态。</summary>
public sealed class GiftPriorityInitializationTests
{
    /// <summary>配置与数据库门槛不同时，启动立即校正未读礼物并保留已读状态。</summary>
    [AvaloniaTheory]
    [InlineData(100, 200, false)]
    [InlineData(200, 100, true)]
    public async Task StartupAppliesSavedGiftThresholdToExistingMessages(int previousMinimum,
        int savedMinimum, bool expectedPriority)
    {
        using var directory = new MessageStoreTestDirectory();
        var unread = Gift("unread");
        var read = Gift("read");
        await using (var store = new SqliteMessageStore(directory.DatabasePath))
        {
            var previous = new MessageReadOptions { GiftAutoPinMinimum = previousMinimum };
            await store.AppendBatchAsync([unread, read], previous);
            await store.MarkReadAsync(read.Id, read.Quantity, read.TotalPrice);
            Assert.Equal(!expectedPriority, (await store.GetByIdAsync(unread.Id))!.IsPriority);
        }
        var configurationStore = new ConfigurationService(directory.Path);
        var configuration = new PanelConfiguration
        {
            Settings = new()
            {
                Reading = new() { GiftAutoPinMinimum = savedMinimum },
                Translation = new() { Enabled = false }
            }
        };
        await configurationStore.SaveAsync(configuration);
        await using var runtime = new PanelRuntime(configurationStore, configurationStore.Load());

        await runtime.InitializeAsync();

        var snapshot = await runtime.Messages.LoadFeedAsync("tab", MessageKind.Gift, 100, null);
        Assert.Equal(expectedPriority ? 2 : 0, snapshot.Records.Count);
        var restored = (await runtime.Messages.GetByIdAsync(unread.Id))!;
        Assert.Equal(expectedPriority, restored.IsPriority);
        Assert.False(restored.IsRead);
        Assert.False(restored.HasBeenRead);
        Assert.Equal(unread.TotalPrice, restored.TotalPrice);
        Assert.Equal(expectedPriority ? 1 : 0, snapshot.UnreadCount);
        var confirmed = (await runtime.Messages.GetByIdAsync(read.Id))!;
        Assert.True(confirmed.IsRead);
        Assert.True(confirmed.HasBeenRead);
        Assert.False(confirmed.IsPriority);
        Assert.Equal(read.Quantity, confirmed.ReadQuantity);
        Assert.Equal(read.TotalPrice, confirmed.ReadTotalPrice);
    }

    private static MessageRecord Gift(string id)
    {
        return new()
        {
            Id = id, RoomTabId = "tab", RoomId = "123", Kind = MessageKind.Gift,
            UserUid = 42, UserName = "viewer", GiftId = "flower", GiftName = "鲜花",
            UnitPrice = 150, TotalPrice = 150
        };
    }
}
