using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views;
using Yohuke.DanmuPanel.Views.Components;
using Xunit;
using Xunit.Abstractions;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>通过真实分栏鼠标拖动记录换行译文的排版成本，并检查拖动持续生效。</summary>
public sealed class TranslationColumnResizePerformanceTests(ITestOutputHelper output)
{
    /// <summary>相同分栏轨迹在有无译文、正文注音时持续更新并保留消息对象。</summary>
    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ContinuousDraggingReflowsTranslatedRows(bool translated, bool pinyin)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var settings = runtime.Configuration.Settings;
        settings.CompactDanmuDisplay = true;
        settings.CompactDanmuWrap = true;
        settings.ShowDanmuPinyin = pinyin;
        settings.AutoScrollLongMessages = false;
        settings.Translation.Enabled = translated;
        var fingerprint = translated ? TranslationRequestFactory.GetConfigurationFingerprint(settings.Translation) : null;
        MessageRecord? pinned = null;
        for (var index = 0; index < 80; index++)
        {
            var record = new MessageRecord
            {
                RoomTabId = "resize", Kind = MessageKind.Danmu, UserName = "viewer",
                Content = "这是用于验证宽度拖动的原文", Time = DateTimeOffset.FromUnixTimeSeconds(1800000000 + index)
            };
            await runtime.Messages.AppendAsync(record, settings.Reading, fingerprint);
            if (translated)
            {
                Assert.True(await runtime.Messages.ApplyTranslationAsync(record.Id, record.Generation, fingerprint!,
                    "The translated message changes its wrapping while the text column is resized."));
            }
            pinned ??= record;
        }
        await runtime.Messages.TogglePinAsync(pinned!.Id);
        var view = new MessageFeedView(runtime, MessageKind.Danmu, exception => throw exception);
        var window = new Window { Width = 900, Height = 600, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "resize" });
            await view.Model.WaitForPresentationAsync();
            window.Show();
            Pump(window);
            var item = Assert.Single(view.Model.PinnedItems);
            var row = Assert.Single(view.GetVisualDescendants().OfType<MessageFeedRow>(), candidate =>
                candidate.DataContext is MessageItemViewModel current && current.Record.Id == pinned.Id);
            var splitter = Assert.Single(row.GetVisualDescendants().OfType<Thumb>(), control => control.Classes.Contains("messageColumnSplitter"));
            var point = splitter.TranslatePoint(new(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), window)!.Value;
            window.MouseMove(point);
            window.MouseDown(point, MouseButton.Left);
            for (var index = 0; index < 8; index++)
            {
                window.MouseMove(point + new Vector(index * 3, 0), RawInputModifiers.LeftMouseButton);
                Pump(window);
            }
            const int STEPS = 120;
            var samples = new double[STEPS];
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            for (var index = 0; index < STEPS; index++)
            {
                var before = Stopwatch.GetTimestamp();
                var phase = index % 40;
                var delta = phase < 20 ? phase * 6 : (40 - phase) * 6;
                window.MouseMove(point + new Vector(delta, 0), RawInputModifiers.LeftMouseButton);
                Pump(window);
                samples[index] = Stopwatch.GetElapsedTime(before).TotalMilliseconds;
            }
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            var changedRatio = settings.MessageColumns.Danmu.CompactIdentityRatio;
            Assert.True(changedRatio > 0.4);
            window.MouseUp(point + new Vector(6, 0), MouseButton.Left);
            Pump(window);
            await view.PendingColumnWidthSave;
            Assert.Same(item, Assert.Single(view.Model.PinnedItems));
            var body = Assert.Single(row.GetVisualDescendants().OfType<Panel>(), panel => panel.Classes.Contains("messageBody"));
            Assert.True(body.Bounds.Width > 0);
            Assert.Equal(translated, body.Children[1].IsVisible);
            Array.Sort(samples);
            var result = new
            {
                translated, pinyin, steps = STEPS, elapsedMs = elapsed, allocatedBytes = allocated,
                p50Ms = samples[STEPS / 2], p95Ms = samples[(int)(STEPS * 0.95)], maxMs = samples[^1],
                realizedRows = view.GetVisualDescendants().OfType<MessageFeedRow>().Count()
            };
            var json = JsonSerializer.Serialize(result);
            output.WriteLine(json);
            if (Environment.GetEnvironmentVariable("DANMUPANEL_RESIZE_METRICS_DIRECTORY") is { Length: > 0 } path)
            {
                Directory.CreateDirectory(path);
                File.WriteAllText(Path.Combine(path, $"resize-{translated}-{pinyin}.json"), json);
            }
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
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
