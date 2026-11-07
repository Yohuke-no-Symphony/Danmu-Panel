using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Xunit.Abstractions;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Models.Workspace;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Tests.Windowing;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>记录包含拼音、表情和缓存头像的纯历史双向滚动成本，验证虚拟化和输入仍然生效。</summary>
public sealed class MessageFeedScrollPerformanceTests(ITestOutputHelper output)
{
    private const int MESSAGE_COUNT = 800;
    private const int USER_COUNT = 32;
    private const int STEPS = 120;

    /// <summary>在生产行样式中反复上下滚动，纯历史操作不重读正文或扩大行容器数量。</summary>
    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BidirectionalHistoryScrollReusesRowsWithoutReadingTheStore(bool pinyin, bool avatars)
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = new PanelConfiguration();
        var settings = configuration.Settings;
        settings.MessageRetentionCount = MESSAGE_COUNT;
        settings.CompactDanmuDisplay = true;
        settings.CompactDanmuWrap = true;
        settings.AutoScrollLongMessages = true;
        settings.ShowUserPinyin = pinyin;
        settings.ShowDanmuPinyin = pinyin;
        settings.ShowUserAvatars = avatars;
        settings.Translation.Enabled = true;
        await using var runtime = new PanelRuntime(new(directory.Path), configuration);
        var imagePath = await PrepareAvatarsAsync(runtime, directory.Path);
        var fingerprint = TranslationRequestFactory.GetConfigurationFingerprint(settings.Translation);
        var records = Enumerable.Range(0, MESSAGE_COUNT).Select(index => new MessageRecord
        {
            Id = "scroll-" + index,
            RoomTabId = "scroll",
            RoomId = "123",
            Kind = MessageKind.Danmu,
            IsTest = true,
            UserUid = index % USER_COUNT + 1,
            UserName = "晚安观众" + index % USER_COUNT,
            Content = index % 4 == 0 ? "晚上好[笑]" : index % 3 == 0 ? "今天的直播很开心大家晚安" : "这首歌很好听",
            Translation = index % 3 == 0 ? "The stream was wonderful. Good night, everyone!" : "Good evening!",
            TranslationFingerprint = fingerprint,
            Time = DateTimeOffset.FromUnixTimeSeconds(1800000000 + index)
        }).ToArray();
        foreach (var batch in records.Chunk(500))
        {
            await runtime.Messages.AppendTestMessagesAsync(batch, settings.Reading, fingerprint);
        }
        for (var index = 0; index < 3; index++)
        {
            await runtime.Messages.TogglePinAsync(records[index].Id);
        }
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, MessageKind.Danmu, errors.Add);
        var window = new Window { Width = 600, Height = 500, Content = view };
        var rows = new HashSet<MessageFeedRow>();
        var texts = new HashSet<EmoticonTextBlock>();
        var preparations = 0;
        var rebuilds = 0;
        var richBuilds = 0;
        var layouts = 0;
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "scroll", RoomId = "123" });
            await view.Model.WaitForPresentationAsync();
            foreach (var item in view.Model.Items.Where(item => item.Record.Content.Contains("[笑]", StringComparison.Ordinal)))
            {
                item.Emoticons = [new DanmuEmoticon { Text = "[笑]", ImagePath = imagePath }];
            }
            window.Show();
            await EmoticonImageTestHelper.LoadAsync(window);
            Pump(window);
            var list = Assert.Single(view.GetVisualDescendants().OfType<ListBox>(), item => item.Name == "ScrollingMessages");
            list.ContainerPrepared += (_, args) =>
            {
                preparations++;
                if (args.Container is MessageFeedListBoxItem container)
                {
                    rows.Add(container.Row);
                    foreach (var text in container.Row.GetVisualDescendants().OfType<EmoticonTextBlock>())
                    {
                        if (texts.Add(text))
                        {
                            text.AnnotationLayoutChanged += () =>
                            {
                                rebuilds++;
                                if (text.HasAnnotations)
                                {
                                    richBuilds++;
                                }
                            };
                        }
                    }
                }
            };
            window.LayoutUpdated += (_, _) => layouts++;
            var viewer = Assert.Single(list.GetVisualDescendants().OfType<ScrollViewer>(), item => item.TemplatedParent == list);
            var presenter = Assert.Single(viewer.GetVisualDescendants().OfType<ScrollContentPresenter>(),
                item => ReferenceEquals(item.TemplatedParent, viewer));
            for (var index = 0; index < 20; index++)
            {
                Scroll(presenter, -60);
                Pump(window);
            }
            await Task.WhenAll(view.GetVisualDescendants().OfType<UserAvatarView>().Select(avatar => avatar.PendingLoad));
            Pump(window);
            preparations = 0;
            rebuilds = 0;
            richBuilds = 0;
            layouts = 0;
            var payloadReads = runtime.Messages.PayloadRowsRead;
            var decodeCountBefore = view.EmoticonBitmaps.DecodeCount;
            var cacheHitsBefore = view.EmoticonBitmaps.HitCount;
            var originals = view.Model.Items.ToArray();
            var samples = new double[STEPS];
            var minimumOffset = viewer.Offset.Y;
            var maximumOffset = minimumOffset;
            var upwardMoves = 0;
            var downwardMoves = 0;
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var startedAt = Stopwatch.GetTimestamp();
            for (var index = 0; index < STEPS; index++)
            {
                var before = Stopwatch.GetTimestamp();
                var previousOffset = viewer.Offset.Y;
                Scroll(presenter, index < STEPS / 2 ? -60 : 60);
                var inputHandled = false;
                Dispatcher.UIThread.Post(() => inputHandled = true, DispatcherPriority.Input);
                Pump(window);
                Assert.True(inputHandled);
                upwardMoves += viewer.Offset.Y < previousOffset ? 1 : 0;
                downwardMoves += viewer.Offset.Y > previousOffset ? 1 : 0;
                minimumOffset = Math.Min(minimumOffset, viewer.Offset.Y);
                maximumOffset = Math.Max(maximumOffset, viewer.Offset.Y);
                samples[index] = Stopwatch.GetElapsedTime(before).TotalMilliseconds;
            }
            var elapsed = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            Array.Sort(samples);
            var result = new
            {
                pinyin, avatars, steps = STEPS, elapsedMs = elapsed, allocatedBytes = allocated,
                p50Ms = samples[STEPS / 2], p95Ms = samples[(int)(STEPS * 0.95)], maxMs = samples[^1],
                preparations, rowInstances = rows.Count, rebuilds, richBuilds, layouts,
                imageDecodes = view.EmoticonBitmaps.DecodeCount - decodeCountBefore,
                imageCacheHits = view.EmoticonBitmaps.HitCount - cacheHitsBefore,
                upwardMoves, downwardMoves, minimumOffset, maximumOffset
            };
            var json = JsonSerializer.Serialize(result);
            output.WriteLine(json);
            if (Environment.GetEnvironmentVariable("DANMUPANEL_SCROLL_METRICS_DIRECTORY") is { Length: > 0 } metricsPath)
            {
                Directory.CreateDirectory(metricsPath);
                File.WriteAllText(Path.Combine(metricsPath, $"scroll-{pinyin}-{avatars}.json"), json);
            }
            Assert.InRange(rows.Count, 1, 128);
            Assert.Equal(payloadReads, runtime.Messages.PayloadRowsRead);
            Assert.Equal(decodeCountBefore, view.EmoticonBitmaps.DecodeCount);
            Assert.True(view.EmoticonBitmaps.HitCount > cacheHitsBefore);
            Assert.Equal(originals, view.Model.Items);
            Assert.True(view.Model.IsReadingHistory);
            Assert.True(upwardMoves > 0 && downwardMoves > 0);
            Assert.True(maximumOffset - minimumOffset > viewer.Viewport.Height);
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    private static async Task<string> PrepareAvatarsAsync(PanelRuntime runtime, string directory)
    {
        Directory.CreateDirectory(directory);
        var imagePath = Path.Combine(directory, "scroll-image.png");
        using (var bitmap = new RenderTargetBitmap(new(64, 64)))
        {
            bitmap.Save(imagePath);
        }
        var avatars = new AppDirectoryService(directory).AvatarCacheDirectory;
        var metadata = Path.Combine(avatars, "Users");
        Directory.CreateDirectory(metadata);
        const string URL = "https://example.invalid/scroll-avatar.png";
        var now = DateTimeOffset.UtcNow;
        for (var userUid = 1; userUid <= USER_COUNT; userUid++)
        {
            var path = runtime.Avatars.GetAvatarFilePath("user-" + userUid, URL);
            File.Copy(imagePath, path);
            await File.WriteAllTextAsync(Path.Combine(metadata, userUid + ".json"), JsonSerializer.Serialize(new UserAvatarCacheEntry
            {
                AvatarUrl = URL, LastCheckedAt = now, LastSuccessAt = now, LastCheckSucceeded = true
            }));
        }
        await Task.WhenAll(Enumerable.Range(1, USER_COUNT).Select(userUid => runtime.UserAvatars.GetAvatarAsync(userUid)));
        return imagePath;
    }

    private static void Scroll(ScrollContentPresenter presenter, double delta)
    {
        presenter.RaiseEvent(new ScrollGestureEventArgs(ScrollGestureEventArgs.GetNextFreeId(), new(0, delta)));
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
