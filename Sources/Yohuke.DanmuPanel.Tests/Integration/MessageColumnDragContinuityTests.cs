using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Views;
using Yohuke.DanmuPanel.Views.Components;
using Xunit;
using Xunit.Abstractions;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证翻译换行及源消息行回收不会结束尚未松开的分栏拖动。</summary>
public sealed class MessageColumnDragContinuityTests(ITestOutputHelper output)
{
    /// <summary>从滚动区拖过翻译换行阈值后仍可继续和反向拖动，源行换绑也不会抢先保存。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DragContinuesAcrossTranslationWrappingAndSourceRowRecycling(bool rebindSource)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var settings = runtime.Configuration.Settings;
        settings.CompactDanmuDisplay = true;
        settings.CompactDanmuWrap = true;
        settings.Translation.Enabled = true;
        settings.AutoScrollLongMessages = false;
        var fingerprint = TranslationRequestFactory.GetConfigurationFingerprint(settings.Translation);
        for (var index = 0; index < 80; index++)
        {
            var record = new MessageRecord
            {
                RoomTabId = "drag", Kind = MessageKind.Danmu, UserName = "viewer", Content = "原文内容",
                Time = DateTimeOffset.FromUnixTimeSeconds(1800000000 + index)
            };
            await runtime.Messages.AppendAsync(record, settings.Reading, fingerprint);
            Assert.True(await runtime.Messages.ApplyTranslationAsync(record.Id, record.Generation, fingerprint,
                "Translated words go here"));
        }
        var view = new MessageFeedView(runtime, MessageKind.Danmu, exception => throw exception);
        var window = new Window { Width = 900, Height = 260, Content = view };
        IPointer? pointer = null;
        var pointerPosition = default(Point);
        window.AddHandler(InputElement.PointerPressedEvent, (_, args) => pointer = args.Pointer, RoutingStrategies.Tunnel, true);
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "drag" });
            await view.Model.WaitForPresentationAsync();
            window.Show();
            Pump(window);
            var list = Assert.Single(view.GetVisualDescendants().OfType<ListBox>(), control => control.Name == "ScrollingMessages");
            var viewer = Assert.Single(list.GetVisualDescendants().OfType<ScrollViewer>(), control => control.TemplatedParent == list);
            var row = list.GetVisualDescendants().OfType<MessageFeedRow>()
                .Where(candidate => candidate.TranslatePoint(default, viewer) is { } point && point.Y >= 0 &&
                    point.Y + candidate.Bounds.Height <= viewer.Viewport.Height)
                .MinBy(candidate => candidate.TranslatePoint(default, viewer)!.Value.Y)!;
            var source = row.DataContext;
            var detached = 0;
            row.DetachedFromVisualTree += (_, _) => detached++;
            var body = Assert.Single(row.GetVisualDescendants().OfType<Panel>(), panel => panel.Classes.Contains("messageBody"));
            Assert.True(body.Children[1].Bounds.X > 0);
            var layout = Assert.Single(row.GetVisualDescendants().OfType<MessageFeedColumnLayout>());
            var adjustableWidth = layout.ColumnDefinitions[1].ActualWidth + layout.ColumnDefinitions[3].ActualWidth;
            var splitter = Assert.Single(row.GetVisualDescendants().OfType<MessageColumnSplitter>());
            var start = splitter.TranslatePoint(new(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), window)!.Value;
            pointerPosition = start;
            window.MouseMove(start);
            window.MouseDown(start, MouseButton.Left);
            Assert.NotNull(pointer);
            Assert.NotNull(pointer.Captured);
            var previous = settings.MessageColumns.Danmu.CompactIdentityRatio;
            var pendingSave = view.PendingColumnWidthSave;
            for (var step = 1; step <= 18; step++)
            {
                pointerPosition = start + new Vector(adjustableWidth * 0.52 * step / 18, 0);
                window.MouseMove(pointerPosition, RawInputModifiers.LeftMouseButton);
                Pump(window);
                if (rebindSource && step == 8)
                {
                    row.DataContext = null;
                    Pump(window);
                }
                var ratio = settings.MessageColumns.Danmu.CompactIdentityRatio;
                output.WriteLine($"step={step}, ratio={ratio:F4}, captured={pointer.Captured?.GetType().Name}, detached={detached}, sourceChanged={!ReferenceEquals(source, row.DataContext)}");
                Assert.NotNull(pointer.Captured);
                Assert.True(ratio > previous, "Each mouse move must keep changing the column width while held.");
                Assert.Same(pendingSave, view.PendingColumnWidthSave);
                previous = ratio;
            }
            Assert.Contains(view.GetVisualDescendants().OfType<Panel>(), panel => panel.Classes.Contains("messageBody") &&
                panel.Children[1].IsVisible && panel.Children[1].Bounds.Y > panel.Children[0].Bounds.Bottom);
            pointerPosition -= new Vector(16, 0);
            window.MouseMove(pointerPosition, RawInputModifiers.LeftMouseButton);
            Pump(window);
            Assert.True(settings.MessageColumns.Danmu.CompactIdentityRatio < previous);
            Assert.NotNull(pointer.Captured);
            window.MouseUp(pointerPosition, MouseButton.Left);
            Pump(window);
            await view.PendingColumnWidthSave;
            Assert.Null(pointer.Captured);
            Assert.Equal(settings.MessageColumns.Danmu.CompactIdentityRatio,
                runtime.ConfigurationStore.Load().Settings.MessageColumns.Danmu.CompactIdentityRatio);
            Assert.Empty(view.Model.PinnedItems);
            Assert.Null(view.CopyPopup);
        }
        finally
        {
            if (window.IsVisible)
            {
                window.MouseUp(pointerPosition, MouseButton.Left);
            }
            view.Model.Dispose();
            window.Close();
        }
    }

    private static void Pump(Window window)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }
}
