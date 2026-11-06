using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Views;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>通过真实消息视图验证外部排版设置变更后的底部跟随及阅读位置。</summary>
public sealed class MessageFeedLayoutFollowTests
{
    /// <summary>字号、边框、紧凑显示与换行改变行高时，跟随状态仍到底部，历史阅读保留可见消息。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.Danmu, nameof(PanelSettings.MessageTextScale), false)]
    [InlineData(MessageKind.Danmu, nameof(PanelSettings.CompactDanmuDisplay), false)]
    [InlineData(MessageKind.Danmu, nameof(PanelSettings.CompactDanmuWrap), false)]
    [InlineData(MessageKind.Danmu, nameof(PanelSettings.MessageTextScale), true)]
    [InlineData(MessageKind.Danmu, nameof(PanelSettings.CompactDanmuDisplay), true)]
    [InlineData(MessageKind.Danmu, nameof(PanelSettings.CompactDanmuWrap), true)]
    [InlineData(MessageKind.SuperChat, nameof(PanelSettings.CompactSuperChatWrap), true)]
    [InlineData(MessageKind.Danmu, nameof(PanelSettings.MessageColumns), false)]
    [InlineData(MessageKind.Danmu, nameof(PanelSettings.MessageColumns), true)]
    [InlineData(MessageKind.SuperChat, nameof(PanelSettings.MessageBorders), true)]
    [InlineData(MessageKind.Gift, nameof(PanelSettings.MessageBorders), true)]
    [InlineData(MessageKind.Guard, nameof(PanelSettings.MessageBorders), true)]
    public async Task LayoutSettingsPreserveFollowOrReadingPosition(MessageKind kind, string setting, bool readingHistory)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var settings = runtime.Configuration.Settings;
        settings.Translation.Enabled = false;
        settings.CompactDanmuDisplay = true;
        settings.CompactDanmuWrap = setting == nameof(PanelSettings.MessageColumns);
        settings.CompactSuperChatWrap = false;
        settings.AutoScrollLongMessages = false;
        var time = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        for (var index = 0; index < 60; index++)
        {
            await runtime.Messages.AppendAsync(new()
            {
                RoomTabId = "layout-room", RoomId = "123", Kind = kind, Time = time.AddSeconds(index),
                UserName = "viewer", TotalPrice = 30,
                Content = string.Concat(Enumerable.Repeat($"layout message {index} ", index % 4 + 3))
            }, settings.Reading);
        }
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, kind, errors.Add);
        var window = new Window { Width = 380, Height = 300, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "layout-room", RoomId = "123" });
            window.Show();
            Pump(window);
            var list = Assert.Single(view.GetVisualDescendants().OfType<ListBox>(), control => control.Name == "ScrollingMessages");
            var viewer = Assert.Single(list.GetVisualDescendants().OfType<ScrollViewer>(), control => control.TemplatedParent == list);
            Assert.InRange(Math.Abs(viewer.ScrollBarMaximum.Y - viewer.Offset.Y), 0, 1);
            Assert.False(view.Model.IsReadingHistory);
            if (readingHistory)
            {
                var presenter = Assert.Single(viewer.GetVisualDescendants().OfType<ScrollContentPresenter>(),
                presenter => ReferenceEquals(presenter.TemplatedParent, viewer));
                presenter.RaiseEvent(new ScrollGestureEventArgs(ScrollGestureEventArgs.GetNextFreeId(),
                    new(0, viewer.ScrollBarMaximum.Y / 3 - viewer.Offset.Y)));
                Pump(window);
                Assert.True(viewer.Offset.Y > 0 && viewer.Offset.Y < viewer.ScrollBarMaximum.Y);
                Assert.True(view.Model.IsReadingHistory);
            }
            var anchor = list.GetRealizedContainers()
                .Where(container => container.TranslatePoint(default, viewer) is { } position &&
                    position.Y + container.Bounds.Height > 0 && position.Y < viewer.Viewport.Height)
                .MinBy(container => container.TranslatePoint(default, viewer)!.Value.Y)!;
            var anchorItem = list.ItemFromContainer(anchor);
            var anchorY = anchor.TranslatePoint(default, viewer)!.Value.Y;
            var initialExtent = viewer.Extent.Height;

            ApplyLayoutChange(settings, setting);
            if (setting is nameof(PanelSettings.MessageColumns) or nameof(PanelSettings.MessageBorders))
            {
                runtime.NotifyAppearanceChanged();
            }
            await view.Model.RefreshAsync();
            Pump(window);

            Assert.True(viewer.Extent.Height > initialExtent,
                $"{setting} did not increase row heights: before={initialExtent}, after={viewer.Extent.Height}");
            Assert.Equal(readingHistory, view.Model.IsReadingHistory);
            if (readingHistory)
            {
                var retainedAnchor = list.ContainerFromItem(anchorItem!);
                Assert.NotNull(retainedAnchor);
                var retainedY = retainedAnchor.TranslatePoint(default, viewer)!.Value.Y;
                Assert.InRange(Math.Abs(retainedY - anchorY), 0, 1);
                Assert.True(viewer.ScrollBarMaximum.Y - viewer.Offset.Y > viewer.Viewport.Height);
            }
            else
            {
                Assert.InRange(Math.Abs(viewer.ScrollBarMaximum.Y - viewer.Offset.Y), 0, 1);
                Assert.NotNull(list.ContainerFromItem(view.Model.ScrollingItems[^1]));
            }
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    private static void ApplyLayoutChange(PanelSettings settings, string setting)
    {
        switch (setting)
        {
            case nameof(PanelSettings.MessageTextScale):
                settings.MessageTextScale = 1.5;
                break;
            case nameof(PanelSettings.CompactDanmuDisplay):
                settings.CompactDanmuDisplay = false;
                break;
            case nameof(PanelSettings.CompactDanmuWrap):
                settings.CompactDanmuWrap = true;
                break;
            case nameof(PanelSettings.CompactSuperChatWrap):
                settings.CompactSuperChatWrap = true;
                break;
            case nameof(PanelSettings.MessageColumns):
                settings.MessageColumns.Danmu.CompactIdentityRatio = 0.75;
                break;
            case nameof(PanelSettings.MessageBorders):
                settings.MessageBorders.SuperChat = 10;
                settings.MessageBorders.Gift = 10;
                settings.MessageBorders.Guard = 10;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(setting));
        }
    }

    private static void Pump(Window window)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }
}
