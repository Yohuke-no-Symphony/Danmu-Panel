using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Models.Workspace;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Tests.Windowing;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证冷表情异步改变行高时的底部跟随、历史阅读锚点和待办期间用户导航。</summary>
public sealed class AsyncEmoticonLayoutTests
{
    /// <summary>图片晚到保持当前阅读策略，解码期间的新导航决定最终位置。</summary>
    [AvaloniaTheory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public async Task DelayedImagesPreserveFollowingOrTheUsersLatestReadingAnchor(bool compact,
        bool readingHistory, bool navigateWhilePending)
    {
        using var directory = new MessageStoreTestDirectory();
        Directory.CreateDirectory(directory.Path);
        var imagePath = Path.Combine(directory.Path, "wide-emoticon.png");
        using (var bitmap = new RenderTargetBitmap(new(1024, 128)))
        {
            bitmap.Save(imagePath);
        }
        var configuration = new PanelConfiguration();
        var settings = configuration.Settings;
        settings.Translation.Enabled = false;
        settings.CompactDanmuDisplay = compact;
        settings.CompactDanmuWrap = true;
        settings.AutoScrollLongMessages = false;
        settings.ShowUserPinyin = false;
        settings.ShowDanmuPinyin = false;
        settings.ShowUserAvatars = false;
        settings.MessageRetentionCount = 100;
        await using var runtime = new PanelRuntime(new(directory.Path), configuration);
        var records = Enumerable.Range(0, 100).Select(index => new MessageRecord
        {
            Id = "late-image-" + index,
            RoomTabId = "late-image",
            RoomId = "123",
            Kind = MessageKind.Danmu,
            IsTest = true,
            UserName = "viewer " + index,
            Content = "[图] 这是一条等待表情载入的历史弹幕 " + index,
            Time = DateTimeOffset.FromUnixTimeSeconds(1800000000 + index)
        }).ToArray();
        await runtime.Messages.AppendTestMessagesAsync(records, settings.Reading);
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, MessageKind.Danmu, errors.Add);
        var window = new Window { Width = 480, Height = 420, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(new RoomTabState { Id = "late-image", RoomId = "123" });
            await view.Model.WaitForPresentationAsync();
            window.Show();
            PinyinTextRenderingTests.Pump(window);
            var list = Assert.Single(view.GetVisualDescendants().OfType<ListBox>(),
                control => control.Name == "ScrollingMessages");
            var viewer = Assert.Single(list.GetVisualDescendants().OfType<ScrollViewer>(),
                control => ReferenceEquals(control.TemplatedParent, list));
            var presenter = Assert.Single(viewer.GetVisualDescendants().OfType<ScrollContentPresenter>(),
                control => ReferenceEquals(control.TemplatedParent, viewer));
            Assert.False(view.Model.IsReadingHistory);
            AssertAtLatest(viewer);
            if (readingHistory)
            {
                Scroll(presenter, viewer.ScrollBarMaximum.Y * 0.45 - viewer.Offset.Y);
                PinyinTextRenderingTests.Pump(window);
                Assert.True(view.Model.IsReadingHistory);
            }

            var payloadReads = runtime.Messages.PayloadRowsRead;
            foreach (var item in view.Model.Items)
            {
                item.Emoticons = [new DanmuEmoticon { Text = "[图]", ImagePath = imagePath }];
            }
            window.UpdateLayout();
            Assert.Contains(view.GetVisualDescendants().OfType<EmoticonTextBlock>(),
                text => text.IsEffectivelyVisible && !text.PendingImageLoad.IsCompleted);
            Assert.Empty(view.GetVisualDescendants().OfType<Image>());
            if (navigateWhilePending)
            {
                var beforeNavigation = viewer.Offset.Y;
                Scroll(presenter, -100);
                window.UpdateLayout();
                Assert.True(viewer.Offset.Y < beforeNavigation);
                Assert.True(view.Model.IsReadingHistory);
            }

            var preserveReading = readingHistory || navigateWhilePending;
            var anchor = preserveReading
                ? FirstVisibleContainer(list, viewer)
                : Assert.IsAssignableFrom<Control>(list.ContainerFromItem(view.Model.ScrollingItems[^1]));
            var anchorItem = Assert.IsType<MessageItemViewModel>(list.ItemFromContainer(anchor));
            var anchorY = anchor.TranslatePoint(default, viewer)!.Value.Y;
            var originalHeight = anchor.Bounds.Height;

            await EmoticonImageTestHelper.LoadAsync(window);

            Assert.NotEmpty(view.GetVisualDescendants().OfType<Image>());
            var retained = Assert.IsAssignableFrom<Control>(list.ContainerFromItem(anchorItem));
            Assert.True(retained.Bounds.Height > originalHeight,
                $"The cold image did not increase the row height: {originalHeight} -> {retained.Bounds.Height}.");
            Assert.Equal(preserveReading, view.Model.IsReadingHistory);
            if (preserveReading)
            {
                Assert.InRange(Math.Abs(retained.TranslatePoint(default, viewer)!.Value.Y - anchorY), 0, 1);
                Assert.True(viewer.ScrollBarMaximum.Y - viewer.Offset.Y > 1);
            }
            else
            {
                AssertAtLatest(viewer);
                Assert.NotNull(list.ContainerFromItem(view.Model.ScrollingItems[^1]));
            }
            Assert.Equal(payloadReads, runtime.Messages.PayloadRowsRead);
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>已缓存的图片在后台验证后更新宽高，保留原来的历史消息和像素阅读位置。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacingACachedImagePreservesTheHistoryAnchor(bool compact)
    {
        using var directory = new MessageStoreTestDirectory();
        Directory.CreateDirectory(directory.Path);
        var imagePath = Path.Combine(directory.Path, "replace-emoticon.png");
        using (var bitmap = new RenderTargetBitmap(new(128, 128)))
        {
            bitmap.Save(imagePath);
        }
        var configuration = new PanelConfiguration();
        var settings = configuration.Settings;
        settings.Translation.Enabled = false;
        settings.CompactDanmuDisplay = compact;
        settings.CompactDanmuWrap = true;
        settings.AutoScrollLongMessages = false;
        settings.ShowUserPinyin = false;
        settings.ShowDanmuPinyin = false;
        settings.ShowUserAvatars = false;
        settings.MessageRetentionCount = 100;
        await using var runtime = new PanelRuntime(new(directory.Path), configuration);
        var records = Enumerable.Range(0, 100).Select(index => new MessageRecord
        {
            Id = "replace-image-" + index,
            RoomTabId = "replace-image",
            RoomId = "123",
            Kind = MessageKind.Danmu,
            IsTest = true,
            UserName = "viewer " + index,
            Content = "[图] 晚安 " + index,
            Time = DateTimeOffset.FromUnixTimeSeconds(1800000000 + index)
        }).ToArray();
        await runtime.Messages.AppendTestMessagesAsync(records, settings.Reading);
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, MessageKind.Danmu, errors.Add);
        var window = new Window { Width = 480, Height = 420, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(new RoomTabState { Id = "replace-image", RoomId = "123" });
            await view.Model.WaitForPresentationAsync();
            foreach (var item in view.Model.Items)
            {
                item.Emoticons = [new DanmuEmoticon { Text = "[图]", ImagePath = imagePath }];
            }
            window.Show();
            await EmoticonImageTestHelper.LoadAsync(window);
            var list = Assert.Single(view.GetVisualDescendants().OfType<ListBox>(),
                control => control.Name == "ScrollingMessages");
            var viewer = Assert.Single(list.GetVisualDescendants().OfType<ScrollViewer>(),
                control => ReferenceEquals(control.TemplatedParent, list));
            var presenter = Assert.Single(viewer.GetVisualDescendants().OfType<ScrollContentPresenter>(),
                control => ReferenceEquals(control.TemplatedParent, viewer));
            Scroll(presenter, viewer.ScrollBarMaximum.Y * 0.45 - viewer.Offset.Y);
            await EmoticonImageTestHelper.LoadAsync(window);
            Assert.True(view.Model.IsReadingHistory);
            var anchor = FirstVisibleContainer(list, viewer);
            var anchorItem = Assert.IsType<MessageItemViewModel>(list.ItemFromContainer(anchor));
            var anchorY = anchor.TranslatePoint(default, viewer)!.Value.Y;
            var originalHeight = anchor.Bounds.Height;
            var originalImageWidth = Assert.Single(anchor.GetVisualDescendants().OfType<Image>()).Bounds.Width;
            var payloadReads = runtime.Messages.PayloadRowsRead;

            using (var bitmap = new RenderTargetBitmap(new(2048, 128)))
            {
                bitmap.Save(imagePath);
            }
            File.SetLastWriteTimeUtc(imagePath, DateTime.UtcNow.AddMinutes(1));
            using var replacement = Assert.IsType<EmoticonBitmapLease>(
                await view.EmoticonBitmaps.AcquireAsync(imagePath, 28));
            await EmoticonImageTestHelper.LoadAsync(window);

            var retained = Assert.IsAssignableFrom<Control>(list.ContainerFromItem(anchorItem));
            var replacementImage = Assert.Single(retained.GetVisualDescendants().OfType<Image>());
            Assert.True(replacementImage.Bounds.Width > originalImageWidth,
                $"The replacement image did not grow wider: {originalImageWidth} -> {replacementImage.Bounds.Width}.");
            Assert.True(retained.Bounds.Height > originalHeight,
                $"The replacement image did not increase row height: {originalHeight} -> {retained.Bounds.Height}.");
            Assert.InRange(Math.Abs(retained.TranslatePoint(default, viewer)!.Value.Y - anchorY), 0, 1);
            Assert.True(view.Model.IsReadingHistory);
            Assert.True(viewer.ScrollBarMaximum.Y - viewer.Offset.Y > viewer.Viewport.Height);
            Assert.Equal(payloadReads, runtime.Messages.PayloadRowsRead);
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    private static Control FirstVisibleContainer(ListBox list, ScrollViewer viewer)
    {
        return list.GetRealizedContainers()
            .Where(container => container.TranslatePoint(default, viewer) is { } position &&
                position.Y + container.Bounds.Height > 0 && position.Y < viewer.Viewport.Height)
            .MinBy(container => container.TranslatePoint(default, viewer)!.Value.Y)!;
    }

    private static void Scroll(ScrollContentPresenter presenter, double delta)
    {
        presenter.RaiseEvent(new ScrollGestureEventArgs(ScrollGestureEventArgs.GetNextFreeId(), new(0, delta)));
    }

    private static void AssertAtLatest(ScrollViewer viewer)
    {
        Assert.InRange(Math.Abs(viewer.ScrollBarMaximum.Y - viewer.Offset.Y), 0, 1);
    }
}
