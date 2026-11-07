using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views;
using Yohuke.DanmuPanel.Views.Components;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证正则屏蔽的原文保留、译文恢复、补翻与菜单状态。</summary>
public sealed class DanmuRegexTranslationViewTests
{
    /// <summary>四种开关组合决定匹配弹幕的可见性及历史补翻，置顶状态保持不变。</summary>
    [AvaloniaTheory]
    [InlineData(false, false, true, 2)]
    [InlineData(false, true, true, 1)]
    [InlineData(true, false, false, 1)]
    [InlineData(true, true, false, 1)]
    public async Task IndependentSwitchesControlVisibilityAndHistoryTranslation(bool enabled, bool blockTranslation,
        bool visible, int pendingCount)
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = new PanelConfiguration
        {
            Settings = new()
            {
                Translation = new() { Enabled = true, Model = "test" },
                DanmuRegexFilter = new() { Rules = [new() { Pattern = "^歌词", BlockDisplay = enabled, BlockTranslation = blockTranslation }] }
            }
        };
        await using var runtime = new PanelRuntime(new(directory.Path), configuration);
        var blocked = new MessageRecord { RoomTabId = "tab", Content = "歌词♪" };
        var ordinary = new MessageRecord { RoomTabId = "tab", Content = "ordinary text" };
        await runtime.Messages.AppendAsync(blocked, new());
        await runtime.Messages.AppendAsync(ordinary, new());
        await runtime.Messages.TogglePinAsync(blocked.Id);
        using var model = new FeedViewModel(runtime, MessageKind.Danmu);

        await model.SelectRoomAsync(new() { Id = "tab" });
        await model.WaitForPresentationAsync();

        Assert.Equal(visible, model.Items.Any(item => item.Record.Id == blocked.Id));
        Assert.Equal(visible, model.PinnedItems.Any(item => item.Record.Id == blocked.Id));
        var pending = await runtime.Messages.GetPendingAsync(10);
        Assert.Equal(pendingCount, pending.Count);
        Assert.Contains(pending, job => job.MessageId == ordinary.Id);
        Assert.True((await runtime.Messages.GetByIdAsync(blocked.Id))!.IsPinned);
        if (visible)
        {
            var item = model.Items.Single(item => item.Record.Id == blocked.Id);
            Assert.Equal(!blockTranslation, item.IsTranslating);
            Assert.True(item.ShowOriginal);
        }
    }

    /// <summary>仅翻译屏蔽隐藏已有译文及等待占位，关闭后原位恢复缓存译文。</summary>
    [AvaloniaTheory]
    [InlineData(false, TranslationDisplayMode.Bilingual)]
    [InlineData(true, TranslationDisplayMode.Bilingual)]
    [InlineData(false, TranslationDisplayMode.TranslationOnly)]
    [InlineData(true, TranslationDisplayMode.TranslationOnly)]
    public async Task TranslationBlockingPreservesOriginalAndRestoresExistingTranslation(bool compact,
        TranslationDisplayMode mode)
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = new PanelConfiguration
        {
            Settings = new()
            {
                CompactDanmuDisplay = compact,
                Translation = new() { Enabled = true, Model = "test", DisplayMode = mode },
                DanmuRegexFilter = new() { Rules = [new() { Pattern = "^歌词", BlockTranslation = true }] }
            }
        };
        await using var runtime = new PanelRuntime(new(directory.Path), configuration);
        var fingerprint = TranslationRequestFactory.GetConfigurationFingerprint(configuration.Settings.Translation);
        var translated = new MessageRecord { RoomTabId = "tab", Content = "歌词♪" };
        var waiting = new MessageRecord { RoomTabId = "tab", Content = "歌词♫" };
        await runtime.Messages.AppendAsync(translated, new(), fingerprint);
        await runtime.Messages.AppendAsync(waiting, new(), fingerprint);
        Assert.True(await runtime.Messages.ApplyTranslationAsync(translated.Id, translated.Generation, fingerprint, "Saved translation"));
        var view = new MessageFeedView(runtime, MessageKind.Danmu, error => throw error);
        var window = new Window { Width = 720, Height = 400, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "tab" });
            await view.Model.WaitForPresentationAsync();
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var item = view.Model.Items.Single(item => item.Record.Id == translated.Id);
            Assert.All(view.Model.Items, message =>
            {
                Assert.False(message.HasTranslation);
                Assert.False(message.IsTranslating);
                Assert.False(message.ShowTranslation);
                Assert.True(message.ShowOriginal);
            });
            Assert.All(view.GetVisualDescendants().OfType<ShimmerTextBlock>(), placeholder =>
                Assert.False(placeholder.IsEffectivelyVisible));
            Assert.Equal("Saved translation", (await runtime.Messages.GetByIdAsync(translated.Id))!.Translation);

            configuration.Settings.DanmuRegexFilter.Rules[0].BlockTranslation = false;
            await runtime.ApplySettingsAsync();
            await view.Model.RefreshAsync();
            await view.Model.WaitForPresentationAsync();

            Assert.Same(item, view.Model.Items.Single(value => value.Record.Id == translated.Id));
            Assert.Equal("Saved translation", item.Translation);
            Assert.True(item.HasTranslation);
            Assert.Equal(mode == TranslationDisplayMode.Bilingual, item.ShowOriginal);
            Assert.True(view.Model.Items.Single(value => value.Record.Id == waiting.Id).IsTranslating);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>重译菜单按消息类型和当前屏蔽规则刷新，执行时再次检查以保留已有译文。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.Danmu, false)]
    [InlineData(MessageKind.SuperChat, true)]
    public async Task RetryMenuRespectsTranslationBlockingAndRefreshesOnOpen(MessageKind kind, bool retryAllowed)
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = new PanelConfiguration
        {
            Settings = new()
            {
                Translation = new() { Enabled = true, Model = "test" },
                DanmuRegexFilter = new() { Rules = [new() { Pattern = "^歌词", BlockTranslation = true }] }
            }
        };
        await using var runtime = new PanelRuntime(new(directory.Path), configuration);
        var fingerprint = TranslationRequestFactory.GetConfigurationFingerprint(configuration.Settings.Translation);
        var record = new MessageRecord { RoomTabId = "tab", Kind = kind, Content = "歌词♪" };
        await runtime.Messages.AppendAsync(record, new(), fingerprint);
        Assert.True(await runtime.Messages.ApplyTranslationAsync(record.Id, record.Generation, fingerprint, "Saved translation"));
        var view = new MessageFeedView(runtime, kind, error => throw error);
        var window = new Window { Width = 720, Height = 400, Content = view };
        ContextMenu? menu = null;
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "tab" });
            await view.Model.WaitForPresentationAsync();
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var row = view.GetVisualDescendants().OfType<MessageFeedRow>()
                .Single(row => row.DataContext is MessageItemViewModel item && item.Record.Id == record.Id);
            menu = row.ContextMenu!;
            menu.Open(row);
            Dispatcher.UIThread.RunJobs();
            var retry = menu.Items.OfType<MenuItem>().Single(item => item.Name == "RetryTranslationMenuItem");
            Assert.Equal(retryAllowed, retry.IsEnabled);
            Assert.Equal(retryAllowed, Assert.Single(view.Model.Items).HasTranslation);
            if (!retryAllowed)
            {
                retry.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.Equal("Saved translation", (await runtime.Messages.GetByIdAsync(record.Id))!.Translation);
                Assert.Empty(await runtime.Messages.GetPendingAsync(10));
            }
            menu.Close();

            configuration.Settings.DanmuRegexFilter.Rules[0].BlockTranslation = false;
            await runtime.ApplySettingsAsync();
            await view.Model.RefreshAsync();
            menu.Open(row);
            Dispatcher.UIThread.RunJobs();

            Assert.True(retry.IsEnabled);
        }
        finally
        {
            menu?.Close();
            view.Model.Dispose();
            window.Close();
        }
    }
}
