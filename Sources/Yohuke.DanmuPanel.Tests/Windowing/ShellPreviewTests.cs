using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.Views.Components;
using Yohuke.DanmuPanel.Views;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Windowing;

/// <summary>使用隔离数据库与真实五面板组件验证工作台，并可输出无网络视觉样例。</summary>
public sealed class ShellPreviewTests
{
    /// <summary>真实消息恢复到各自面板，渲染中英日内容、已读与置顶状态。</summary>
    [AvaloniaFact]
    public async Task RealPanelsRenderSavedMessagesWithoutNetwork()
    {
        var directory = Path.Combine(Path.GetTempPath(), "danmu-panel-preview", Guid.NewGuid().ToString("N"));
        var configurationStore = new ConfigurationService(directory);
        var room = new RoomTabState { RoomId = "123456", Name = "夜更音乐电台" };
        var workspace = new WorkspaceState { Name = "直播工作区", Rooms = [room], SelectedRoomId = room.Id };
        var configuration = new PanelConfiguration { Workspaces = [workspace] };
        configuration.Settings.Translation.Enabled = false;
        configuration.Settings.Translation.TargetLanguage = "ja-JP";
        configuration.Settings.ShowUserPinyin = true;
        configuration.Settings.ShowGiftPinyin = true;
        configuration.Settings.Reading = new() { GiftAutoPinMinimum = 50 };
        configuration.Settings.SelectedWorkspaceId = workspace.Id;
        var runtime = new PanelRuntime(configurationStore, configuration);
        var errors = new List<Exception>();
        var originalLanguage = Localization.SelectedLanguageID;
        var originalTheme = Application.Current!.RequestedThemeVariant;
        Localization.SetLanguage("zh-CN");
        Application.Current.RequestedThemeVariant = ThemeVariant.Light;
        var workspaceView = new WorkspaceView(runtime, errors.Add, new Workspace.FakeRoomAvatarService());
        using var testView = new TestMessagesView(runtime, errors.Add);
        testView.SelectRoom(room);
        var kinds = new[] { MessageKind.Danmu, MessageKind.SuperChat, MessageKind.Gift, MessageKind.Guard };
        var feeds = kinds.Select(kind => new MessageFeedView(runtime, kind, errors.Add)).ToArray();
        var panels = new Dictionary<string, Control>
        {
            ["workspace"] = workspaceView, ["danmu"] = feeds[0], ["superChat"] = feeds[1],
            ["gift"] = feeds[2], ["guard"] = feeds[3], ["testMessages"] = testView
        };
        var shell = new ShellWindow(panels, workspaceView.Toolbar, directory);
        workspaceView.HostWindow = shell;
        try
        {
            var generation = await runtime.Messages.GetGenerationAsync(room.Id);
            var time = new DateTimeOffset(2026, 11, 4, 20, 30, 0, TimeSpan.FromHours(8));
            var first = await AddAsync(runtime, room, generation, MessageKind.Danmu, "月光旅人", "今晚的歌声真好听！", time,
                translation: "今夜の歌声は本当に素敵ですね！", medal: "夜更", medalLevel: 24);
            await runtime.Messages.TogglePinAsync(first.Id);
            await AddAsync(runtime, room, generation, MessageKind.Danmu, "Sakura", "今日も配信ありがとう！", time.AddSeconds(12),
                translation: "今天也谢谢你的直播！");
            await AddAsync(runtime, room, generation, MessageKind.Danmu, "Alex", "This is my favorite song. Thank you!", time.AddSeconds(10),
                translation: "这是我最喜欢的歌，谢谢！");
            await AddAsync(runtime, room, generation, MessageKind.Danmu, "星河", "刚刚那一段可以再唱一遍吗？", time.AddSeconds(8),
                translation: "さっきの部分をもう一度歌ってもらえますか？", medal: "夜更", medalLevel: 18);
            await AddAsync(runtime, room, generation, MessageKind.Danmu, "木漏れ日", "おつかれさま！ゆっくり休んでね。", time.AddSeconds(6),
                translation: "辛苦啦！记得好好休息。");
            await AddAsync(runtime, room, generation, MessageKind.Danmu, "海风", "今天也要开心呀 ✨", time.AddSeconds(4),
                translation: "今日も楽しい一日になりますように ✨");
            await AddAsync(runtime, room, generation, MessageKind.SuperChat, "小林", "谢谢你的陪伴，祝每一天都快乐！", time.AddSeconds(30),
                price: 100, translation: "いつもそばにいてくれてありがとう。毎日が幸せでありますように！", color: "#1D71B8");
            var readSc = await AddAsync(runtime, room, generation, MessageKind.SuperChat, "Luna", "Keep singing. We are always here for you.", time.AddSeconds(20),
                price: 30, translation: "これからも歌い続けてね。私たちはいつも応援しています。", color: "#3676BD");
            await runtime.Messages.MarkReadAsync(readSc.Id, readSc.Quantity, readSc.TotalPrice);
            await AddAsync(runtime, room, generation, MessageKind.Gift, "星海", "", time.AddSeconds(40), price: 100, gift: "小电视飞船", quantity: 1);
            var readGift = await AddAsync(runtime, room, generation, MessageKind.Gift, "晚风", "", time.AddSeconds(35), price: 60, gift: "心动盲盒", quantity: 6);
            await runtime.Messages.MarkReadAsync(readGift.Id, readGift.Quantity, readGift.TotalPrice);
            await AddAsync(runtime, room, generation, MessageKind.Gift, "青空", "", time.AddSeconds(33), price: 3, gift: "打call", quantity: 3);
            await AddAsync(runtime, room, generation, MessageKind.Guard, "春山", "", time.AddSeconds(45), guardLevel: 3, color: "#2A60B2");
            var guard = await AddAsync(runtime, room, generation, MessageKind.Guard, "南星", "", time.AddSeconds(42), guardLevel: 2, color: "#9554B2");
            await runtime.Messages.MarkReadAsync(guard.Id, guard.Quantity, guard.TotalPrice);
            foreach (var feed in feeds)
            {
                await feed.Model.SelectRoomAsync(room);
            }
            await shell.InitializeLayoutAsync();
            shell.Show();
            Dispatcher.UIThread.RunJobs();
            shell.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(errors);
            Assert.False(Assert.Single(shell.GetVisualDescendants().OfType<WindowTitleBar>()).FindControl<TextBlock>("TitleText")!.IsVisible);
            Assert.True(feeds[0].Model.Items[0].HasTranslation);
            Assert.Equal(6, feeds[0].Model.Items.Count);
            Assert.True(feeds[0].Model.Items[0].Record.IsPinned);
            Assert.All(feeds, feed => Assert.NotEmpty(feed.Model.Items));
            Assert.True(feeds[1].Model.Items[^1].Record.IsRead);
            if (Environment.GetEnvironmentVariable("DANMUPANEL_SCREENSHOT") is { Length: > 0 } screenshot)
            {
                using var frame = shell.CaptureRenderedFrame();
                Assert.NotNull(frame);
                frame.Save(screenshot);
                configuration.Settings.CompactDanmuDisplay = false;
                foreach (var feed in feeds)
                {
                    await feed.Model.RefreshAsync();
                }
                shell.UpdateLayout();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                Dispatcher.UIThread.RunJobs();
                using var regularFrame = shell.CaptureRenderedFrame();
                Assert.NotNull(regularFrame);
                regularFrame.Save(Path.Combine(Path.GetDirectoryName(screenshot)!,
                    Path.GetFileNameWithoutExtension(screenshot) + "-regular.png"));
                configuration.Settings.CompactDanmuDisplay = true;
                foreach (var feed in feeds)
                {
                    await feed.Model.RefreshAsync();
                }
                shell.ShowPanel("testMessages");
                shell.UpdateLayout();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                Dispatcher.UIThread.RunJobs();
                using var testFrame = shell.CaptureRenderedFrame();
                Assert.NotNull(testFrame);
                testFrame.Save(Path.Combine(Path.GetDirectoryName(screenshot)!,
                    Path.GetFileNameWithoutExtension(screenshot) + "-testing.png"));
            }
        }
        finally
        {
            await testView.FinishPendingAsync();
            await shell.CloseAsync();
            foreach (var feed in feeds)
            {
                feed.Model.Dispose();
            }
            workspaceView.Dispose();
            await runtime.DisposeAsync();
            Localization.SetLanguage(originalLanguage);
            Application.Current.RequestedThemeVariant = originalTheme;
            Directory.Delete(directory, true);
        }
    }

    private static async Task<MessageRecord> AddAsync(PanelRuntime runtime, RoomTabState room, long generation,
        MessageKind kind, string userName, string content, DateTimeOffset time, decimal price = 0,
        string? translation = null, string gift = "", int quantity = 1, int guardLevel = 0,
        string color = "", string medal = "", int medalLevel = 0)
    {
        var fingerprint = TranslationRequestFactory.GetConfigurationFingerprint(runtime.Configuration.Settings.Translation);
        var result = await runtime.Messages.AppendAsync(new()
        {
            RoomTabId = room.Id, RoomId = room.RoomId, Generation = generation, Kind = kind,
            UserName = userName, UserUid = 10001, Content = content, Time = time, TotalPrice = price,
            UnitPrice = price / quantity, Quantity = quantity, GiftName = gift, GuardLevel = guardLevel,
            BorderColor = color, MedalName = medal, MedalLevel = medalLevel
        }, runtime.Configuration.Settings.Reading, translation is null ? null : fingerprint);
        Assert.NotNull(result.Record);
        if (translation is not null)
        {
            Assert.True(await runtime.Messages.ApplyTranslationAsync(result.Record.Id, generation, fingerprint, translation));
        }
        return result.Record;
    }
}
