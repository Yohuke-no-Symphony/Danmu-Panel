using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views;
using Yohuke.DanmuPanel.Views.Components;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证置顶区与普通弹幕区在独立滚动时仍共享同一条分栏边界。</summary>
public sealed class PinnedFeedColumnAlignmentTests
{
    /// <summary>任一区域的滚动条出现或隐藏后，两个区域的分栏及正文起点仍对齐。</summary>
    [AvaloniaTheory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task IndependentScrollbarsKeepColumnsAligned(bool compact, bool pinnedOverflows, bool scrollingOverflows)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory, compact);
        await SeedAsync(runtime, pinnedOverflows ? 30 : 1, scrollingOverflows ? 30 : 1);
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, MessageKind.Danmu, errors.Add);
        var window = new Window { Width = 720, Height = 500, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "alignment", RoomId = "123" });
            await view.Model.WaitForPresentationAsync();
            window.Show();
            Pump(window);
            var pinned = FindList(view, "PinnedMessages");
            var scrolling = FindList(view, "ScrollingMessages");
            Assert.Equal(pinnedOverflows, Overflows(pinned));
            Assert.Equal(scrollingOverflows, Overflows(scrolling));
            AssertAligned(view, pinned, scrolling);
            SavePreview(window, $"pinned-columns-{compact}-{pinnedOverflows}-{scrollingOverflows}");

            window.Width = 430;
            Pump(window);
            AssertAligned(view, pinned, scrolling);

            var pinnedExtent = FindViewer(pinned).Extent.Height;
            var scrollingExtent = FindViewer(scrolling).Extent.Height;
            window.Height = Math.Max(pinnedExtent * 3, pinnedExtent + scrollingExtent) + 160;
            Pump(window);
            Assert.False(Overflows(pinned));
            Assert.False(Overflows(scrolling));
            AssertAligned(view, pinned, scrolling);

            window.Height = 500;
            Pump(window);
            Assert.Equal(pinnedOverflows, Overflows(pinned));
            Assert.Equal(scrollingOverflows, Overflows(scrolling));
            AssertAligned(view, pinned, scrolling);
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>隐藏置顶区不再贡献旧滚动条占位，重新显示和重新附着后仍恢复对齐。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HiddenPinnedRegionAndReattachmentDoNotRetainStaleGutters(bool compact)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory, compact);
        var records = await SeedAsync(runtime, 20, 1);
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, MessageKind.Danmu, errors.Add);
        var window = new Window { Width = 640, Height = 400, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "alignment", RoomId = "123" });
            await view.Model.WaitForPresentationAsync();
            window.Show();
            Pump(window);
            var pinned = FindList(view, "PinnedMessages");
            var scrolling = FindList(view, "ScrollingMessages");
            window.Height = FindViewer(pinned).Extent.Height * 2 + 100;
            Pump(window);
            Assert.True(Overflows(pinned));
            Assert.False(Overflows(scrolling));
            AssertAligned(view, pinned, scrolling);
            Assert.True(scrolling.Margin.Right > 0);

            foreach (var record in records.Take(20))
            {
                await runtime.Messages.TogglePinAsync(record.Id);
            }
            await view.Model.RefreshAsync();
            await view.Model.WaitForPresentationAsync();
            Pump(window);
            Assert.False(pinned.IsEffectivelyVisible);
            Assert.False(Overflows(scrolling));
            Assert.Equal(new Thickness(0), scrolling.Margin);

            foreach (var record in records.Take(20))
            {
                await runtime.Messages.TogglePinAsync(record.Id);
            }
            await view.Model.RefreshAsync();
            await view.Model.WaitForPresentationAsync();
            Pump(window);
            AssertAligned(view, pinned, scrolling);

            window.Content = null;
            Pump(window);
            Assert.Equal(new Thickness(0), pinned.Margin);
            Assert.Equal(new Thickness(0), scrolling.Margin);
            window.Content = view;
            Pump(window);
            AssertAligned(view, pinned, scrolling);
            Assert.True(scrolling.Margin.Right > 0);
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>补偿保留原始边距且不会反复触发布局，释放后还原边距并停止更新。</summary>
    [AvaloniaFact]
    public void AlignmentPreservesMarginsAndStopsUpdatingAfterDisposal()
    {
        var pinnedMargin = new Thickness(3, 2, 9, 4);
        var scrollingMargin = new Thickness(3, 2, 5, 4);
        var pinned = new ListBox { ItemsSource = new[] { "置顶" }, Margin = pinnedMargin };
        var scrolling = new ListBox
        {
            ItemsSource = Enumerable.Range(0, 60).Select(index => "弹幕 " + index).ToArray(),
            Margin = scrollingMargin
        };
        Grid.SetRow(scrolling, 1);
        var host = new Grid { RowDefinitions = new("80,*"), Children = { pinned, scrolling } };
        using var alignment = new MessageFeedViewportAlignment(pinned, scrolling);
        var window = new Window { Width = 500, Height = 300, Content = host };
        try
        {
            window.Show();
            Pump(window);
            Assert.True(pinned.Margin.Right > pinnedMargin.Right);
            Assert.Equal(pinnedMargin.Left, pinned.Margin.Left);
            Assert.Equal(pinnedMargin.Top, pinned.Margin.Top);
            Assert.Equal(pinnedMargin.Bottom, pinned.Margin.Bottom);
            Assert.Equal(scrollingMargin.Left, scrolling.Margin.Left);
            Assert.Equal(scrollingMargin.Top, scrolling.Margin.Top);
            Assert.Equal(scrollingMargin.Bottom, scrolling.Margin.Bottom);
            Assert.InRange(Math.Abs(FindPresenter(pinned).Bounds.Width - FindPresenter(scrolling).Bounds.Width), 0, 0.5);
            var changes = 0;
            pinned.PropertyChanged += (_, args) =>
            {
                if (args.Property == Layoutable.MarginProperty)
                {
                    changes++;
                }
            };
            scrolling.PropertyChanged += (_, args) =>
            {
                if (args.Property == Layoutable.MarginProperty)
                {
                    changes++;
                }
            };
            Pump(window);
            Assert.Equal(0, changes);

            alignment.Dispose();
            Assert.Equal(pinnedMargin, pinned.Margin);
            Assert.Equal(scrollingMargin, scrolling.Margin);
            window.Width = 650;
            Pump(window);
            Assert.Equal(pinnedMargin, pinned.Margin);
            Assert.Equal(scrollingMargin, scrolling.Margin);
        }
        finally
        {
            window.Close();
        }
    }

    private static PanelRuntime CreateRuntime(MessageStoreTestDirectory directory, bool compact)
    {
        return new(new(directory.Path), new()
        {
            Settings = new() { CompactDanmuDisplay = compact, AutoScrollLongMessages = false }
        });
    }

    private static async Task<MessageRecord[]> SeedAsync(PanelRuntime runtime, int pinnedCount, int scrollingCount)
    {
        var records = new List<MessageRecord>();
        for (var index = 0; index < pinnedCount + scrollingCount; index++)
        {
            var record = new MessageRecord
            {
                RoomTabId = "alignment", RoomId = "123", Kind = MessageKind.Danmu,
                UserName = "viewer", Content = "正文", Time = DateTimeOffset.FromUnixTimeSeconds(1800000000 + index)
            };
            await runtime.Messages.AppendAsync(record, runtime.Configuration.Settings.Reading);
            if (index < pinnedCount)
            {
                await runtime.Messages.TogglePinAsync(record.Id);
            }
            records.Add(record);
        }
        return [.. records];
    }

    private static void AssertAligned(Control relativeTo, ListBox pinned, ListBox scrolling)
    {
        var pinnedRow = FindVisibleRow(pinned);
        var scrollingRow = FindVisibleRow(scrolling);
        var pinnedSplitter = Assert.Single(pinnedRow.GetVisualDescendants().OfType<MessageColumnSplitter>(),
            value => value.IsEffectivelyVisible);
        var scrollingSplitter = Assert.Single(scrollingRow.GetVisualDescendants().OfType<MessageColumnSplitter>(),
            value => value.IsEffectivelyVisible);
        var pinnedX = pinnedSplitter.TranslatePoint(new(pinnedSplitter.Bounds.Width / 2, 0), relativeTo)!.Value.X;
        var scrollingX = scrollingSplitter.TranslatePoint(new(scrollingSplitter.Bounds.Width / 2, 0), relativeTo)!.Value.X;
        Assert.InRange(Math.Abs(pinnedX - scrollingX), 0, 0.5);
        var pinnedBody = Assert.Single(pinnedRow.GetVisualDescendants().OfType<Panel>(), panel => panel.Classes.Contains("messageBody") && panel.IsEffectivelyVisible);
        var scrollingBody = Assert.Single(scrollingRow.GetVisualDescendants().OfType<Panel>(), panel => panel.Classes.Contains("messageBody") && panel.IsEffectivelyVisible);
        Assert.InRange(Math.Abs(pinnedBody.TranslatePoint(default, relativeTo)!.Value.X -
            scrollingBody.TranslatePoint(default, relativeTo)!.Value.X), 0, 0.5);
        Assert.InRange(Math.Abs(pinnedRow.Bounds.Width - scrollingRow.Bounds.Width), 0, 0.5);
    }

    private static MessageFeedRow FindVisibleRow(ListBox list)
    {
        var presenter = FindPresenter(list);
        return list.GetVisualDescendants().OfType<MessageFeedRow>().First(row =>
            row.DataContext is MessageItemViewModel && row.TranslatePoint(default, presenter) is { } position &&
            position.Y + row.Bounds.Height > 0 && position.Y < presenter.Bounds.Height);
    }

    private static ListBox FindList(Control view, string name)
    {
        return Assert.Single(view.GetVisualDescendants().OfType<ListBox>(), list => list.Name == name);
    }

    private static ScrollViewer FindViewer(ListBox list)
    {
        return Assert.Single(list.GetVisualDescendants().OfType<ScrollViewer>(), viewer => ReferenceEquals(viewer.TemplatedParent, list));
    }

    private static ScrollContentPresenter FindPresenter(ListBox list)
    {
        var viewer = FindViewer(list);
        return Assert.Single(viewer.GetVisualDescendants().OfType<ScrollContentPresenter>(), presenter =>
            ReferenceEquals(presenter.TemplatedParent, viewer));
    }

    private static bool Overflows(ListBox list)
    {
        var viewer = FindViewer(list);
        return viewer.Extent.Height > viewer.Viewport.Height + 0.5;
    }

    private static void SavePreview(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("DANMUPANEL_PINNED_ALIGNMENT_PREVIEW_DIRECTORY") is { Length: > 0 } path)
        {
            Directory.CreateDirectory(path);
            using var frame = window.CaptureRenderedFrame();
            frame!.Save(Path.Combine(path, name + ".png"));
        }
    }

    private static void Pump(Window window)
    {
        for (var pass = 0; pass < 8; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }
}
