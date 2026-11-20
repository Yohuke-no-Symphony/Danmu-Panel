using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Models.Workspace;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Tests.Workspace;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views;
using Yohuke.DanmuPanel.Views.Components;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证所有消息类型的头像位置、缩放与设置刷新。</summary>
public sealed class MessageFeedAvatarTests
{
    /// <summary>四类消息的圆形头像均位于名字左侧，开关与缩放原位生效。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.Danmu)]
    [InlineData(MessageKind.SuperChat)]
    [InlineData(MessageKind.Gift)]
    [InlineData(MessageKind.Guard)]
    public async Task AllMessageKindsShowScaledAvatarToTheLeftOfName(MessageKind kind)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var settings = runtime.Configuration.Settings;
        settings.CompactDanmuDisplay = false;
        await runtime.Messages.AppendAsync(CreateMessage(kind, 0), settings.Reading);
        var view = new MessageFeedView(runtime, kind, exception => throw exception);
        var window = new Window { Content = view, Width = 760, Height = 300 };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "tab", RoomId = "123" });
            window.Show();
            Pump(window);
            var item = Assert.Single(view.Model.Items);
            var row = Assert.Single(view.GetVisualDescendants().OfType<MessageFeedRow>());
            var avatar = Assert.Single(row.GetVisualDescendants().OfType<UserAvatarView>(), value => value.IsEffectivelyVisible);
            var name = Assert.Single(row.GetVisualDescendants().OfType<EmoticonTextBlock>(),
                text => text.Classes.Contains("danmuFeedUser") && text.IsEffectivelyVisible);
            Assert.True(avatar.IsEffectivelyVisible);
            Assert.Equal(TextAlignment.Left, name.TextAlignment);
            Assert.Equal(20, avatar.Bounds.Width);
            Assert.Equal(avatar.Bounds.Width, avatar.Bounds.Height);
            Assert.True(avatar.TranslatePoint(new(avatar.Bounds.Width, 0), row)!.Value.X <=
                name.TranslatePoint(default, row)!.Value.X);

            settings.CompactDanmuDisplay = true;
            settings.MessageTextScale = 1.5;
            await view.Model.RefreshAsync();
            Pump(window);
            Assert.Equal(24, avatar.Bounds.Width);
            Assert.Same(item, Assert.Single(view.Model.Items));

            settings.ShowUserAvatars = false;
            await runtime.ApplySettingsAsync();
            await view.Model.RefreshAsync();
            Pump(window);
            Assert.False(avatar.IsVisible);
            Assert.Null(avatar.AvatarImage);
            Assert.Same(item, Assert.Single(view.Model.Items));

            settings.ShowUserAvatars = true;
            await runtime.ApplySettingsAsync();
            await view.Model.RefreshAsync();
            Pump(window);
            Assert.True(avatar.IsEffectivelyVisible);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>在尾部追加并裁剪旧弹幕时，保留在视口里的旧行继续使用各自的头像。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FollowingAnAppendKeepsAvatarsOfRetainedVisibleRows(bool trim)
    {
        using var directory = new MessageStoreTestDirectory();
        Directory.CreateDirectory(directory.Path);
        var imagePath = Path.Combine(directory.Path, "avatar.png");
        using (var image = new WriteableBitmap(new(80, 80), new(96, 96), Avalonia.Platform.PixelFormat.Bgra8888))
        {
            image.Save(imagePath);
        }
        var service = new ControlledUserAvatarService { Resolve = (_, _) => Task.FromResult(imagePath) };
        var settings = new PanelSettings();
        var pinyin = new PinyinService();
        var original = Enumerable.Range(1, 40).Select(uid =>
            new MessageItemViewModel(CreateMessage(MessageKind.Danmu, uid), settings, pinyin)).ToArray();
        var added = new MessageItemViewModel(CreateMessage(MessageKind.Danmu, 41), settings, pinyin);
        var source = new FeedCollection<MessageItemViewModel>();
        source.InsertRange(0, original);
        var list = new MessageFeedListBox(() => new(MessageKind.Danmu, service)) { ItemsSource = source };
        var window = new Window { Content = list, Width = 640, Height = 350 };
        try
        {
            window.Show();
            Pump(window);
            var viewer = Assert.IsType<ScrollViewer>(list.Scroll);
            using var scroll = new DanmuFeedScrollController(viewer, list);
            scroll.Attach(source);
            await WaitForAvatarAsync(original[^1].UserUid);
            var existing = Avatars().Where(avatar => avatar.AvatarImage is not null).ToDictionary(avatar => avatar.UserUid,
                avatar => (View: avatar, Bitmap: avatar.AvatarImage));
            var requestCount = service.RequestedUsers.Count;

            if (trim)
            {
                source.RemoveRange(0, 1);
            }
            source.InsertRange(source.Count, [added]);
            await WaitForAvatarAsync(added.UserUid);

            var retained = Avatars().Where(avatar => existing.ContainsKey(avatar.UserUid)).ToArray();
            Assert.NotEmpty(retained);
            Assert.All(retained, avatar =>
            {
                Assert.Same(existing[avatar.UserUid].View, avatar);
                Assert.Same(existing[avatar.UserUid].Bitmap, avatar.AvatarImage);
            });
            Assert.DoesNotContain(service.RequestedUsers.Skip(requestCount), existing.ContainsKey);
            Assert.Equal(added.UserUid, Assert.Single(service.RequestedUsers.Skip(requestCount), uid => uid == added.UserUid));
        }
        finally
        {
            window.Close();
        }

        IEnumerable<UserAvatarView> Avatars()
        {
            return list.GetRealizedContainers().OfType<MessageFeedListBoxItem>().SelectMany(container =>
                container.Row.GetVisualDescendants().OfType<UserAvatarView>());
        }

        async Task WaitForAvatarAsync(long uid)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                Pump(window);
                if (Avatars().Any(avatar => avatar.UserUid == uid && avatar.AvatarImage is not null))
                {
                    return;
                }
                await Task.Delay(10, timeout.Token);
            }
        }
    }

    /// <summary>实际直播提交与底部跟随期间，保留在视口的旧弹幕沿用各自的头像。</summary>
    [AvaloniaTheory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(true, 4)]
    public async Task LiveAppendKeepsExistingThumbnailsWithVariableRowHeights(bool compact, int batchSize)
    {
        const int INITIAL_COUNT = 80;
        const int ROUNDS = 6;
        using var directory = new MessageStoreTestDirectory();
        var settings = new PanelSettings
        {
            CompactDanmuDisplay = compact, CompactDanmuWrap = true, AutoScrollLongMessages = false,
            ShowDanmuPinyin = true, ShowUserPinyin = true
        };
        var room = new RoomTabState { Id = "tab", RoomId = "123" };
        await using var runtime = new PanelRuntime(new(directory.Path), new PanelConfiguration { Settings = settings });
        await SeedThumbnailsAsync(runtime, directory.Path, INITIAL_COUNT + ROUNDS * batchSize);
        var start = DateTimeOffset.UtcNow;
        var records = Enumerable.Range(1, INITIAL_COUNT).Select(uid => CreateDanmu(uid) with { IsTest = true }).ToArray();
        await runtime.Messages.AppendTestMessagesAsync(records, settings.Reading);
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, MessageKind.Danmu, errors.Add);
        var window = new Window { Content = view, Width = 450, Height = 420 };
        try
        {
            await view.Model.SelectRoomAsync(room);
            await view.Model.WaitForPresentationAsync();
            window.Show();
            Pump(window);
            var list = Assert.Single(view.GetVisualDescendants().OfType<MessageFeedListBox>(),
                candidate => candidate.Name == "ScrollingMessages");
            var viewer = Assert.IsType<ScrollViewer>(list.Scroll);
            await WaitForFeedAsync(INITIAL_COUNT);
            for (var round = 0; round < ROUNDS; round++)
            {
                var before = VisibleAvatars().ToDictionary(avatar => avatar.UserUid,
                    avatar => (View: avatar, Image: avatar.AvatarImage));
                var first = INITIAL_COUNT + 1 + round * batchSize;
                var results = await runtime.Messages.AppendBatchAsync(Enumerable.Range(first, batchSize)
                    .Select(CreateDanmu).ToArray(), settings.Reading);
                foreach (var result in results)
                {
                    runtime.PublishCommittedMessage(Assert.IsType<MessageRecord>(result.Record));
                }
                await WaitForFeedAsync(first + batchSize - 1);
                var retained = VisibleAvatars().Where(avatar => before.ContainsKey(avatar.UserUid)).ToArray();
                Assert.NotEmpty(retained);
                foreach (var avatar in retained)
                {
                    var previous = before[avatar.UserUid];
                    Assert.True(ReferenceEquals(previous.Image, avatar.AvatarImage),
                        $"Batch {round}, user {avatar.UserUid}: same control={ReferenceEquals(previous.View, avatar)}.");
                }
            }
            Assert.Empty(errors);

            IEnumerable<UserAvatarView> VisibleAvatars()
            {
                return list.GetVisualDescendants().OfType<UserAvatarView>().Where(avatar =>
                    avatar.IsEffectivelyVisible && avatar.TranslatePoint(default, viewer) is { } position &&
                    position.Y < viewer.Viewport.Height && position.Y + avatar.Bounds.Height > 0);
            }

            async Task WaitForFeedAsync(int latestUid)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                while (true)
                {
                    Pump(window);
                    var avatars = VisibleAvatars().ToArray();
                    if (view.Model.ScrollingItems[^1].UserUid == latestUid && avatars.Any(avatar => avatar.UserUid == latestUid) &&
                        avatars.All(avatar => avatar.AvatarImage is not null) && viewer.ScrollBarMaximum.Y - viewer.Offset.Y <= 1)
                    {
                        await view.Model.WaitForPresentationAsync().WaitAsync(timeout.Token);
                        Pump(window);
                        return;
                    }
                    await Task.Delay(10, timeout.Token);
                }
            }
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }

        MessageRecord CreateDanmu(int uid)
        {
            return CreateMessage(MessageKind.Danmu, uid) with
            {
                Id = $"live-avatar-{uid}", UserName = $"直播观众 {uid}", Time = start.AddMilliseconds(uid),
                Content = uid % 4 == 0 ? "晚安" : string.Concat(Enumerable.Repeat("今天的直播很开心，明天再见。", uid % 4))
            };
        }
    }

    private static async Task SeedThumbnailsAsync(PanelRuntime runtime, string directory, int count)
    {
        const string URL = "https://example.invalid/feed-avatar.png";
        var avatarDirectory = new AppDirectoryService(directory).AvatarCacheDirectory;
        var metadataDirectory = Path.Combine(avatarDirectory, "Users");
        Directory.CreateDirectory(metadataDirectory);
        using var bitmap = new WriteableBitmap(new(96, 96), new(96, 96), Avalonia.Platform.PixelFormat.Bgra8888);
        var now = DateTimeOffset.UtcNow;
        var metadata = JsonSerializer.Serialize(new UserAvatarCacheEntry
        {
            AvatarUrl = URL, LastCheckedAt = now, LastSuccessAt = now, LastCheckSucceeded = true
        });
        for (var uid = 1; uid <= count; uid++)
        {
            bitmap.Save(runtime.Avatars.GetAvatarFilePath("user-" + uid, URL));
            await File.WriteAllTextAsync(Path.Combine(metadataDirectory, uid + ".json"), metadata);
        }
    }

    /// <summary>生成真实圆形图片的宽松和紧凑布局预览，所有窗口在完成后关闭。</summary>
    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RenderAvatarPreviews(bool compact, bool narrow)
    {
        using var directory = new MessageStoreTestDirectory();
        var imagePath = Path.Combine(directory.Path, "avatar.png");
        Directory.CreateDirectory(directory.Path);
        using (var image = new RenderTargetBitmap(new(128, 128)))
        {
            var picture = new Grid
            {
                Background = new SolidColorBrush(Color.Parse("#6D6EDB")),
                Children =
                {
                    new TextBlock
                    {
                        Text = "夜", Foreground = Brushes.White, FontSize = 76,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                    }
                }
            };
            picture.Measure(new(128, 128));
            picture.Arrange(new(0, 0, 128, 128));
            image.Render(picture);
            image.Save(imagePath);
        }
        var service = new ControlledUserAvatarService { Resolve = (_, _) => Task.FromResult(imagePath) };
        var settings = new PanelSettings { CompactDanmuDisplay = compact };
        var content = new StackPanel { Spacing = 12, Margin = new Thickness(16) };
        var surface = new Grid { Children = { content } };
        surface.Classes.Set("compactDanmu", compact);
        var rows = new List<MessageFeedRow>();
        foreach (var kind in Enum.GetValues<MessageKind>())
        {
            var label = new TextBlock { Text = kind.ToString(), FontSize = 13, Foreground = Brushes.Gray };
            var row = new MessageFeedRow(kind, service)
            {
                DataContext = new MessageItemViewModel(CreateMessage(kind, 12345), settings, new PinyinService())
            };
            rows.Add(row);
            content.Children.Add(new StackPanel { Spacing = 4, Children = { label, row } });
        }
        var window = new Window
        {
            Content = surface, Width = narrow ? 380 : 820, Height = 520, Background = Brushes.White,
            RequestedThemeVariant = ThemeVariant.Light
        };
        try
        {
            window.Show();
            Pump(window);
            var avatars = rows.Select(row => Assert.Single(row.GetVisualDescendants().OfType<UserAvatarView>(),
                value => value.IsEffectivelyVisible)).ToArray();
            for (var attempt = 0; attempt < 100 && avatars.Any(avatar => avatar.AvatarImage is null); attempt++)
            {
                await Task.Delay(10);
                Pump(window);
            }
            Assert.All(avatars, avatar => Assert.NotNull(avatar.AvatarImage));
            Pump(window);
            var previewDirectory = Path.Combine(Path.GetTempPath(), "danmaku-panel-previews");
            Directory.CreateDirectory(previewDirectory);
            using var preview = window.CaptureRenderedFrame();
            preview!.Save(Path.Combine(previewDirectory,
                $"user-avatars-{(compact ? "compact" : "normal")}-{(narrow ? "narrow" : "wide")}.png"));
        }
        finally
        {
            window.Close();
        }
    }

    private static MessageRecord CreateMessage(MessageKind kind, long userUid)
    {
        return new()
        {
            RoomTabId = "tab", RoomId = "123", UserUid = userUid, Kind = kind,
            UserName = "夜更小芙", Content = "晚上好！今天的直播也很开心。", MedalName = "夜更中", MedalLevel = 20,
            GiftName = "小花花", Quantity = 3, UnitPrice = 10, TotalPrice = 30, GuardLevel = 3
        };
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
