using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
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

/// <summary>使用真实鼠标输入验证消息分栏、独立密度偏好及虚拟化阅读位置。</summary>
public sealed class MessageFeedColumnResizeTests
{
    /// <summary>三类横向消息在两种密度下同步列宽并保存，拖动不触发消息阅读操作。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.Danmu, false)]
    [InlineData(MessageKind.Danmu, true)]
    [InlineData(MessageKind.Gift, false)]
    [InlineData(MessageKind.Gift, true)]
    [InlineData(MessageKind.Guard, false)]
    [InlineData(MessageKind.Guard, true)]
    public async Task DraggingColumnsPersistsWithoutActivatingMessages(MessageKind kind, bool compact)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory, compact);
        var records = await SeedAsync(runtime, kind, 4);
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, kind, errors.Add);
        var window = new Window { Width = 720, Height = 420, Content = view };
        try
        {
            await ShowAsync(window, view);
            var before = GetRatio(runtime.Configuration.Settings, kind, compact);
            var originalItems = view.Model.Items.ToArray();
            var row = Rows(view).First();
            var initialLayout = Layout(row);
            var adjustableWidth = Column(initialLayout, 1).Bounds.Width + Column(initialLayout, 3).Bounds.Width;
            var medalWidths = MedalWidths(view);
            var settingsChanges = 0;
            runtime.Changed += _ => settingsChanges++;

            Drag(window, row, 80);
            await view.PendingColumnWidthSave;
            Pump(window);

            var after = GetRatio(runtime.Configuration.Settings, kind, compact);
            Assert.True(after > before + 0.05);
            Assert.InRange(Math.Abs(after - before - 80 / adjustableWidth), 0, 0.005);
            AssertRatios(view, after);
            Assert.Equal(after, GetRatio(runtime.ConfigurationStore.Load().Settings, kind, compact));
            Assert.Equal(compact ? 0.32 : 0.46, GetRatio(runtime.Configuration.Settings, kind, !compact));
            Assert.Equal(originalItems, view.Model.Items);
            Assert.Equal(0, settingsChanges);
            Assert.Equal(medalWidths, MedalWidths(view));
            if (kind == MessageKind.Danmu)
            {
                Assert.NotEmpty(medalWidths);
                Assert.All(medalWidths, width => Assert.Equal(66, width));
            }
            else
            {
                Assert.Empty(medalWidths);
            }
            foreach (var record in records)
            {
                var stored = (await runtime.Messages.GetByIdAsync(record.Id))!;
                Assert.False(stored.IsPinned);
                Assert.False(stored.IsRead);
            }
            Assert.Null(view.CopyPopup);
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>仅点击手柄不激活消息，方向键微调同步并保存列宽。</summary>
    [AvaloniaFact]
    public async Task SplitterClickAndKeyboardAdjustmentDoNotActivateTheMessage()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory, true);
        var record = Assert.Single(await SeedAsync(runtime, MessageKind.Danmu, 1));
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, MessageKind.Danmu, errors.Add);
        var window = new Window { Width = 640, Height = 300, Content = view };
        try
        {
            await ShowAsync(window, view);
            var row = Assert.Single(Rows(view));
            var point = SplitterPoint(row, window);
            var before = GetRatio(runtime.Configuration.Settings, MessageKind.Danmu, true);
            window.MouseMove(point);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Pump(window);
            Assert.Equal(before, GetRatio(runtime.Configuration.Settings, MessageKind.Danmu, true));

            var splitter = Assert.Single(row.GetVisualDescendants().OfType<MessageColumnSplitter>());
            Assert.True(splitter.Focus());
            window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
            window.KeyRelease(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
            await view.PendingColumnWidthSave;
            Pump(window);

            var after = GetRatio(runtime.Configuration.Settings, MessageKind.Danmu, true);
            Assert.InRange(Math.Abs(after - before - 0.02), 0, 0.000001);
            AssertRatios(view, after);
            Assert.Equal(after, GetRatio(runtime.ConfigurationStore.Load().Settings, MessageKind.Danmu, true));
            var stored = (await runtime.Messages.GetByIdAsync(record.Id))!;
            Assert.False(stored.IsPinned);
            Assert.False(stored.IsRead);
            Assert.Null(view.CopyPopup);
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>宽松和紧凑比例可分别调整，切换回原密度时恢复其最后一次选择。</summary>
    [AvaloniaFact]
    public async Task SwitchingDensityRestoresEachLayoutsLastWidth()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory, false);
        await SeedAsync(runtime, MessageKind.Danmu, 3);
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, MessageKind.Danmu, errors.Add);
        var window = new Window { Width = 720, Height = 400, Content = view };
        try
        {
            await ShowAsync(window, view);
            Drag(window, Rows(view).First(), 65);
            await view.PendingColumnWidthSave;
            var regular = GetRatio(runtime.Configuration.Settings, MessageKind.Danmu, false);

            runtime.Configuration.Settings.CompactDanmuDisplay = true;
            await view.Model.RefreshAsync();
            Pump(window);
            AssertRatios(view, 0.46);
            Drag(window, Rows(view).First(), -50);
            await view.PendingColumnWidthSave;
            var compact = GetRatio(runtime.Configuration.Settings, MessageKind.Danmu, true);
            Assert.True(compact < 0.46);

            runtime.Configuration.Settings.CompactDanmuDisplay = false;
            await view.Model.RefreshAsync();
            Pump(window);
            AssertRatios(view, regular);
            runtime.Configuration.Settings.CompactDanmuDisplay = true;
            await view.Model.RefreshAsync();
            Pump(window);
            AssertRatios(view, compact);
            var restored = runtime.ConfigurationStore.Load().Settings.MessageColumns.GetWidths(MessageKind.Danmu);
            Assert.Equal(regular, restored.RegularIdentityRatio);
            Assert.Equal(compact, restored.CompactIdentityRatio);
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>同类视图共享拖动结果，同一密度下的外部设置通知也会重取列宽。</summary>
    [AvaloniaFact]
    public async Task SameKindViewsAndAppearanceNotificationsRefreshColumnsWithoutReloadingRows()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory, true);
        await SeedAsync(runtime, MessageKind.Danmu, 3);
        var errors = new List<Exception>();
        var first = new MessageFeedView(runtime, MessageKind.Danmu, errors.Add);
        var second = new MessageFeedView(runtime, MessageKind.Danmu, errors.Add);
        var host = new Grid { ColumnDefinitions = new("*,*"), Children = { first, second } };
        Grid.SetColumn(second, 1);
        var window = new Window { Width = 1100, Height = 360, Content = host };
        try
        {
            await first.Model.SelectRoomAsync(Room());
            await second.Model.SelectRoomAsync(Room());
            await first.Model.WaitForPresentationAsync();
            await second.Model.WaitForPresentationAsync();
            window.Show();
            Pump(window);
            var firstItems = first.Model.Items.ToArray();
            var secondItems = second.Model.Items.ToArray();

            Drag(window, Rows(first).First(), 45);
            await first.PendingColumnWidthSave;
            Pump(window);
            var changed = GetRatio(runtime.Configuration.Settings, MessageKind.Danmu, true);
            AssertRatios(first, changed);
            AssertRatios(second, changed);

            runtime.Configuration.Settings.MessageColumns = new()
            {
                Danmu = new() { RegularIdentityRatio = 0.37, CompactIdentityRatio = 0.58 }
            };
            runtime.NotifyAppearanceChanged();
            Pump(window);
            AssertRatios(first, 0.58);
            AssertRatios(second, 0.58);
            Assert.Equal(firstItems, first.Model.Items);
            Assert.Equal(secondItems, second.Model.Items);
            Assert.Empty(errors);
        }
        finally
        {
            first.Model.Dispose();
            second.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>置顶行和滚动后新实现的消息容器继承同一比例，粉丝牌宽度保持不变。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PinnedAndRecycledRowsInheritTheCurrentWidth(bool compact)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory, compact);
        var records = await SeedAsync(runtime, MessageKind.Danmu, 140);
        await runtime.Messages.TogglePinAsync(records[0].Id);
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, MessageKind.Danmu, errors.Add);
        var window = new Window { Width = 640, Height = 340, Content = view };
        try
        {
            await ShowAsync(window, view);
            var list = FindList(view, "ScrollingMessages");
            var viewer = FindViewer(list);
            var initialIds = Rows(list).Select(row => ((MessageItemViewModel)row.DataContext!).Record.Id).ToHashSet();
            var row = FullyVisibleRows(list, viewer).Last();

            Drag(window, row, 55);
            await view.PendingColumnWidthSave;
            Pump(window);
            var ratio = GetRatio(runtime.Configuration.Settings, MessageKind.Danmu, compact);
            var pinned = FindList(view, "PinnedMessages");
            Assert.NotEmpty(Rows(pinned));
            AssertRatios(view, ratio);

            ScrollTo(viewer, 0);
            Pump(window);
            Assert.Contains(Rows(list), candidate => !initialIds.Contains(((MessageItemViewModel)candidate.DataContext!).Record.Id));
            AssertRatios(view, ratio);
            Assert.All(MedalWidths(view), width => Assert.Equal(66, width));
            Assert.True((await runtime.Messages.GetByIdAsync(records[0].Id))!.IsPinned);
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>加载骨架与真实消息继承祖先比例，调整后两者的正文边界继续对齐。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SkeletonsAndRowsShareInheritedColumnsAndFixedMedals(bool compact)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory, compact);
        using var model = new FeedViewModel(runtime, MessageKind.Danmu);
        var row = new MessageFeedRow(MessageKind.Danmu)
        {
            DataContext = new MessageItemViewModel(Message(MessageKind.Danmu, 0), runtime.Configuration.Settings, runtime.Pinyin)
        };
        var skeleton = new MessageFeedSkeleton { Kind = MessageKind.Danmu };
        var surface = new MessageFeedSurface { DataContext = model };
        surface.Host.Content = new StackPanel { Children = { row, skeleton } };
        surface.Resources["MessageFontSizeBody"] = 13d;
        surface.Resources["MessageFontSizeCompact"] = 12d;
        surface.Resources["MessageFontSizeMetadata"] = 11d;
        surface.Resources["MessageFontSizePinyin"] = 10d;
        surface.Resources["MessageMedalSlotWidth"] = 66d;
        surface.Resources["MessageMedalLevelWidth"] = 20d;
        surface.SetValue(MessageFeedColumnLayout.IdentityRatioProperty, 0.35);
        var window = new Window { Width = 640, Height = 300, Content = surface };
        try
        {
            window.Show();
            Pump(window);
            var medalWidths = MedalWidths(surface);
            var body = BodyX(skeleton, surface);

            surface.SetValue(MessageFeedColumnLayout.IdentityRatioProperty, 0.64);
            Pump(window);

            AssertRatio(Layout(row), 0.64);
            AssertRatio(Layout(skeleton), 0.64);
            Assert.InRange(Math.Abs(BodyX(row, surface) - BodyX(skeleton, surface)), 0, 1);
            Assert.True(BodyX(skeleton, surface) > body + 50);
            Assert.Equal(medalWidths, MedalWidths(surface));
            Assert.False(skeleton.IsHitTestVisible);
            Assert.Equal(compact ? 3 : 4, skeleton.PlaceholderRectangles.Count);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>丢失捕获或移除视图后保存已应用的比例，重新附着后仍能继续拖动。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedDragPersistsAndAllowsFurtherInteraction(bool detach)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory, true);
        var records = await SeedAsync(runtime, MessageKind.Danmu, 3);
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, MessageKind.Danmu, errors.Add);
        var window = new Window { Width = 640, Height = 360, Content = view };
        try
        {
            await ShowAsync(window, view);
            IPointer? pointer = null;
            window.AddHandler(InputElement.PointerPressedEvent, (_, args) => pointer = args.Pointer,
                RoutingStrategies.Tunnel, true);
            var start = SplitterPoint(Rows(view).First(), window);
            window.MouseMove(start);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(start + new Vector(70, 0), RawInputModifiers.LeftMouseButton);
            Pump(window);
            var changed = GetRatio(runtime.Configuration.Settings, MessageKind.Danmu, true);
            Assert.True(changed > 0.46);
            if (detach)
            {
                window.Content = null;
            }
            else
            {
                Assert.NotNull(pointer);
                pointer.Capture(null);
            }
            Pump(window);
            await view.PendingColumnWidthSave;
            window.MouseUp(start + new Vector(70, 0), MouseButton.Left);
            Assert.Equal(changed, GetRatio(runtime.ConfigurationStore.Load().Settings, MessageKind.Danmu, true));
            if (detach)
            {
                window.Content = view;
                Pump(window);
            }

            Drag(window, Rows(view).First(), -25);
            await view.PendingColumnWidthSave;
            Assert.True(GetRatio(runtime.Configuration.Settings, MessageKind.Danmu, true) < changed);
            foreach (var record in records)
            {
                var stored = (await runtime.Messages.GetByIdAsync(record.Id))!;
                Assert.False(stored.IsPinned);
                Assert.False(stored.IsRead);
            }
            Assert.Null(view.CopyPopup);
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>窄面板和字号缩放下的两端拖动保留有效列宽，粉丝牌不参与压缩。</summary>
    [AvaloniaTheory]
    [InlineData(false, 1d)]
    [InlineData(true, 1d)]
    [InlineData(false, 1.5d)]
    [InlineData(true, 1.5d)]
    public async Task ExtremeDragsKeepBothColumnsUsableAndMedalsFixed(bool compact, double scale)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory, compact);
        runtime.Configuration.Settings.MessageTextScale = scale;
        await SeedAsync(runtime, MessageKind.Danmu, 2);
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, MessageKind.Danmu, errors.Add);
        var window = new Window { Width = 320, Height = 400, Content = view };
        try
        {
            await ShowAsync(window, view);
            foreach (var delta in new[] { -2000d, 2000d })
            {
                var row = Rows(view).First();
                Drag(window, row, delta);
                await view.PendingColumnWidthSave;
                Pump(window);
                var layout = Layout(row);
                var identity = Column(layout, 1);
                var body = Column(layout, 3);
                var ratio = GetRatio(runtime.Configuration.Settings, MessageKind.Danmu, compact);
                Assert.True(double.IsFinite(ratio) && ratio > 0 && ratio < 1);
                Assert.True(identity.Bounds.Width > 0);
                Assert.True(body.Bounds.Width > 0);
                Assert.True(body.Bounds.Left >= identity.Bounds.Right);
                Assert.True(body.Bounds.Right <= layout.Bounds.Width + 1);
                Assert.All(MedalWidths(view), width => Assert.Equal(66 * scale, width));
            }
            Assert.Null(view.CopyPopup);
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>拖窄正文改变行高时继续跟随底部，历史阅读则保持原消息及像素锚点。</summary>
    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DraggingWrappedContentPreservesFollowOrReadingAnchor(bool compact, bool readingHistory)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory, compact);
        runtime.Configuration.Settings.CompactDanmuWrap = true;
        await SeedAsync(runtime, MessageKind.Danmu, 60, true);
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, MessageKind.Danmu, errors.Add);
        var window = new Window { Width = 460, Height = 340, Content = view };
        try
        {
            await ShowAsync(window, view);
            var list = FindList(view, "ScrollingMessages");
            var viewer = FindViewer(list);
            if (readingHistory)
            {
                ScrollTo(viewer, viewer.ScrollBarMaximum.Y / 3);
                Pump(window);
                Assert.True(view.Model.IsReadingHistory);
            }
            var anchor = list.GetRealizedContainers()
                .Where(container => container.TranslatePoint(default, viewer) is { } point &&
                    point.Y + container.Bounds.Height > 0 && point.Y < viewer.Viewport.Height)
                .MinBy(container => container.TranslatePoint(default, viewer)!.Value.Y)!;
            var anchorItem = list.ItemFromContainer(anchor);
            var anchorY = anchor.TranslatePoint(default, viewer)!.Value.Y;
            var initialExtent = viewer.Extent.Height;
            var row = FullyVisibleRows(list, viewer).Last();

            Drag(window, row, 110);
            await view.PendingColumnWidthSave;
            Pump(window);

            Assert.True(viewer.Extent.Height > initialExtent);
            Assert.Equal(readingHistory, view.Model.IsReadingHistory);
            if (readingHistory)
            {
                var retained = Assert.IsAssignableFrom<Control>(list.ContainerFromItem(anchorItem!));
                Assert.InRange(Math.Abs(retained.TranslatePoint(default, viewer)!.Value.Y - anchorY), 0, 1);
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

    private static PanelRuntime CreateRuntime(MessageStoreTestDirectory directory, bool compact)
    {
        var configuration = new PanelConfiguration
        {
            Settings = new()
            {
                CompactDanmuDisplay = compact,
                AutoScrollLongMessages = false,
                Reading = new() { GiftAutoPinMinimum = 1000 },
                MessageColumns = new()
                {
                    Danmu = new() { RegularIdentityRatio = 0.32, CompactIdentityRatio = 0.46 },
                    Gift = new() { RegularIdentityRatio = 0.32, CompactIdentityRatio = 0.46 },
                    Guard = new() { RegularIdentityRatio = 0.32, CompactIdentityRatio = 0.46 }
                }
            }
        };
        return new(new(directory.Path), configuration);
    }

    private static async Task<MessageRecord[]> SeedAsync(PanelRuntime runtime, MessageKind kind, int count, bool longContent = false)
    {
        var records = new List<MessageRecord>();
        for (var index = 0; index < count; index++)
        {
            var record = Message(kind, index);
            if (longContent)
            {
                record = record with { Content = string.Concat(Enumerable.Repeat($"message {index} ", index % 3 + 5)) };
            }
            await runtime.Messages.AppendAsync(record, runtime.Configuration.Settings.Reading);
            records.Add(record);
        }
        return records.ToArray();
    }

    private static MessageRecord Message(MessageKind kind, int index)
    {
        return new()
        {
            RoomTabId = "column-room", RoomId = "123", Kind = kind,
            UserName = "viewer", Content = $"message {index}", GiftName = "礼物", GuardLevel = 3,
            MedalName = "粉丝牌", MedalLevel = 20, TotalPrice = 30,
            Time = DateTimeOffset.FromUnixTimeSeconds(1800000000 + index)
        };
    }

    private static RoomTabState Room()
    {
        return new() { Id = "column-room", RoomId = "123" };
    }

    private static async Task ShowAsync(Window window, MessageFeedView view)
    {
        await view.Model.SelectRoomAsync(Room());
        await view.Model.WaitForPresentationAsync();
        window.Show();
        Pump(window);
        Assert.NotEmpty(Rows(view));
    }

    private static double GetRatio(PanelSettings settings, MessageKind kind, bool compact)
    {
        var widths = settings.MessageColumns.GetWidths(kind);
        return compact ? widths.CompactIdentityRatio : widths.RegularIdentityRatio;
    }

    private static MessageFeedRow[] Rows(Visual owner)
    {
        return owner.GetVisualDescendants().OfType<MessageFeedRow>()
            .Where(row => row.DataContext is MessageItemViewModel && row.IsEffectivelyVisible).ToArray();
    }

    private static MessageFeedRow[] FullyVisibleRows(ListBox list, ScrollViewer viewer)
    {
        return Rows(list).Where(row => row.TranslatePoint(default, viewer) is { } point &&
            point.Y >= 0 && point.Y + row.Bounds.Height <= viewer.Viewport.Height).ToArray();
    }

    private static MessageFeedColumnLayout Layout(Visual owner)
    {
        return Assert.Single(owner.GetVisualDescendants().OfType<MessageFeedColumnLayout>());
    }

    private static Control Column(MessageFeedColumnLayout layout, int column)
    {
        return Assert.Single(layout.Children, child => Grid.GetColumn(child) == column);
    }

    private static void AssertRatios(Visual owner, double ratio)
    {
        var rows = Rows(owner);
        Assert.NotEmpty(rows);
        foreach (var row in rows)
        {
            AssertRatio(Layout(row), ratio);
        }
    }

    private static void AssertRatio(MessageFeedColumnLayout layout, double ratio)
    {
        var identity = Column(layout, 1).Bounds.Width;
        var body = Column(layout, 3).Bounds.Width;
        Assert.True(identity > 0 && body > 0);
        Assert.InRange(Math.Abs(identity / (identity + body) - ratio), 0, 0.01);
    }

    private static double BodyX(Visual owner, Visual relativeTo)
    {
        return Column(Layout(owner), 3).TranslatePoint(default, relativeTo)!.Value.X;
    }

    private static double[] MedalWidths(Visual owner)
    {
        return owner.GetVisualDescendants().OfType<Grid>().Where(grid => grid.Classes.Contains("danmuMedalSlot"))
            .Select(grid => grid.Bounds.Width).ToArray();
    }

    private static Point SplitterPoint(MessageFeedRow row, Window window)
    {
        var splitter = Assert.Single(row.GetVisualDescendants().OfType<Thumb>(),
            candidate => candidate.Classes.Contains("messageColumnSplitter"));
        Assert.True(splitter.Bounds.Width > 0 && splitter.Bounds.Height > 0);
        return splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), window)!.Value;
    }

    private static void Drag(Window window, MessageFeedRow row, double delta)
    {
        var start = SplitterPoint(row, window);
        window.MouseMove(start);
        window.MouseDown(start, MouseButton.Left);
        for (var step = 1; step <= 3; step++)
        {
            window.MouseMove(start + new Vector(delta * step / 3, 0), RawInputModifiers.LeftMouseButton);
            Pump(window);
        }
        window.MouseUp(start + new Vector(delta, 0), MouseButton.Left);
        Pump(window);
    }

    private static ListBox FindList(MessageFeedView view, string name)
    {
        return Assert.Single(view.GetVisualDescendants().OfType<ListBox>(), list => list.Name == name);
    }

    private static ScrollViewer FindViewer(ListBox list)
    {
        return Assert.Single(list.GetVisualDescendants().OfType<ScrollViewer>(), viewer => viewer.TemplatedParent == list);
    }

    private static void ScrollTo(ScrollViewer viewer, double offset)
    {
        var presenter = Assert.Single(viewer.GetVisualDescendants().OfType<ScrollContentPresenter>(),
            candidate => ReferenceEquals(candidate.TemplatedParent, viewer));
        presenter.RaiseEvent(new ScrollGestureEventArgs(ScrollGestureEventArgs.GetNextFreeId(), new(0, offset - viewer.Offset.Y)));
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
