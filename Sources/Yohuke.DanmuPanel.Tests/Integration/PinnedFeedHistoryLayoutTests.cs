using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Xunit;
using Xunit.Abstractions;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证加载更早弹幕后，置顶移组的滚动位置与虚拟化行布局。</summary>
public sealed class PinnedFeedHistoryLayoutTests(ITestOutputHelper output)
{
    /// <summary>置顶或取消置顶固定最顶部可见的其他真实消息（包含半行），并保持行顺序、边界与阅读策略。</summary>
    [AvaloniaTheory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task PinAndUnpinPreserveLayoutAfterLoadingEarlierMessages(bool loadEarlier, bool returnToLatest,
        bool useMenu)
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = new PanelConfiguration();
        var settings = configuration.Settings;
        settings.Translation.Enabled = false;
        settings.ShowUserPinyin = true;
        settings.ShowDanmuPinyin = true;
        settings.AutoScrollLongMessages = false;
        var fingerprint = TranslationRequestFactory.GetConfigurationFingerprint(settings.Translation);
        await using var runtime = new PanelRuntime(new(directory.Path), configuration);
        for (var index = 0; index < 310; index++)
        {
            await runtime.Messages.AppendAsync(new()
            {
                RoomTabId = "tab",
                RoomId = "123",
                Kind = MessageKind.Danmu,
                UserName = index % 3 == 0 ? "viewer" : "宇神",
                Content = index % 3 == 0 ? $"message {index}" : $"晚安 {index}",
                MedalName = "牵丝绳",
                MedalLevel = index % 32 + 1,
                Translation = index % 3 == 0 ? "" : "おやすみなさい",
                TranslationFingerprint = fingerprint,
                Time = DateTimeOffset.FromUnixTimeSeconds(1800000000 + index)
            }, settings.Reading);
        }
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, MessageKind.Danmu, errors.Add);
        var window = new Window { Width = 450, Height = 350, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "tab", RoomId = "123" });
            await view.Model.WaitForPresentationAsync();
            window.Show();
            Pump(window);
            var list = FindList(view, "ScrollingMessages");
            var viewer = FindViewer(list);
            var focusedContainer = VisibleContainers(list, viewer)[^1];
            Assert.True(focusedContainer.Focus());
            Assert.Same(focusedContainer, KeyboardNavigation.GetTabOnceActiveElement(list));
            var presenter = Assert.Single(viewer.GetVisualDescendants().OfType<ScrollContentPresenter>(),
                presenter => ReferenceEquals(presenter.TemplatedParent, viewer));
            presenter.RaiseEvent(new ScrollGestureEventArgs(ScrollGestureEventArgs.GetNextFreeId(), new(0, -viewer.Offset.Y)));
            Pump(window);
            Assert.DoesNotContain(focusedContainer, list.GetRealizedContainers());
            Assert.True(focusedContainer.IsEffectivelyVisible);
            Assert.Contains(focusedContainer, list.ItemsPanelRoot!.GetVisualChildren());
            if (loadEarlier)
            {
                if (useMenu)
                {
                    ActivateMenuItem(list, "Panel.MoreHistory", window);
                    for (var attempt = 0; attempt < 100 && view.Model.ScrollingItems.Count < 200; attempt++)
                    {
                        await Task.Delay(10);
                        Pump(window);
                    }
                }
                else
                {
                    await view.Model.LoadMoreAsync();
                }
                await view.Model.WaitForPresentationAsync();
                Pump(window);
                Assert.Equal(200, view.Model.ScrollingItems.Count);
            }
            foreach (var fraction in new[] { 0d, 0.25, 0.5, 0.75 })
            {
                presenter.RaiseEvent(new ScrollGestureEventArgs(ScrollGestureEventArgs.GetNextFreeId(),
                    new(0, viewer.ScrollBarMaximum.Y * fraction - viewer.Offset.Y)));
                Pump(window);
                if (fraction > 0)
                {
                    Assert.True(viewer.Offset.Y > 0);
                }
            }
            if (returnToLatest)
            {
                var actions = Assert.IsType<StackPanel>(view.HeaderActionsTemplate.Build(view));
                var latest = actions.Children.OfType<Button>().Single(button => button.Name == "BackToLatestButton");
                await Assert.IsAssignableFrom<IAsyncRelayCommand>(latest.Command).ExecuteAsync(null);
                Pump(window);
            }
            await view.Model.WaitForPresentationAsync();
            Pump(window);
            AssertLayout(list, viewer, "before pin");
            Assert.True(list.GetRealizedContainers().Select(container => container.Bounds.Height).Distinct().Count() > 1);
            for (var pass = 0; pass < 3; pass++)
            {
                var visible = VisibleContainers(list, viewer);
                Assert.True(visible.Length >= 3);
                var clicked = visible[1];
                var target = Assert.IsType<MessageItemViewModel>(list.ItemFromContainer(clicked));
                var retained = TopVisibleNeighbor(list, viewer, target);
                var neighbor = Assert.IsType<MessageItemViewModel>(list.ItemFromContainer(retained));
                var before = retained.TranslatePoint(default, viewer)!.Value.Y;
                await ActivateAsync(clicked, target, true, useMenu, view, window);
                output.WriteLine($"pin {pass}: offset={viewer.Offset.Y}, maximum={viewer.ScrollBarMaximum.Y}, history={view.Model.IsReadingHistory}");
                AssertLayout(list, viewer, $"after pin {pass}");
                if (returnToLatest)
                {
                    AssertAtLatest(view, viewer);
                }
                else
                {
                    Assert.True(view.Model.IsReadingHistory);
                    AssertReadingPosition(list, viewer, neighbor, before);
                }
                var pinnedList = FindList(view, "PinnedMessages");
                AssertLayout(pinnedList, FindViewer(pinnedList), $"pinned {pass}");
                var pinned = Assert.IsAssignableFrom<Control>(pinnedList.ContainerFromItem(target));
                retained = TopVisibleNeighbor(list, viewer, target);
                neighbor = Assert.IsType<MessageItemViewModel>(list.ItemFromContainer(retained));
                before = retained.TranslatePoint(default, viewer)!.Value.Y;
                await ActivateAsync(pinned, target, false, useMenu, view, window);
                output.WriteLine($"unpin {pass}: offset={viewer.Offset.Y}, maximum={viewer.ScrollBarMaximum.Y}, history={view.Model.IsReadingHistory}");
                AssertLayout(list, viewer, $"after unpin {pass}");
                if (returnToLatest)
                {
                    AssertAtLatest(view, viewer);
                }
                else
                {
                    Assert.True(view.Model.IsReadingHistory);
                    AssertReadingPosition(list, viewer, neighbor, before);
                }
            }
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    private static async Task ActivateAsync(Control container, MessageItemViewModel item, bool pinned,
        bool useMenu, MessageFeedView view, Window window)
    {
        var row = Assert.Single(container.GetVisualDescendants().OfType<MessageFeedRow>());
        if (useMenu)
        {
            ActivateMenuItem(row, pinned ? "Panel.PinMessage" : "Panel.UnpinMessage", window);
        }
        else
        {
            var point = MessageBodyPoint(row, window);
            window.MouseMove(point);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
        }
        for (var attempt = 0; attempt < 100 && item.Record.IsPinned != pinned; attempt++)
        {
            await Task.Delay(10);
            Pump(window);
        }
        Assert.Equal(pinned, item.Record.IsPinned);
        await view.Model.RefreshAsync();
        await view.Model.WaitForPresentationAsync();
        Pump(window);
    }

    private static void ActivateMenuItem(Control owner, string key, Window window)
    {
        var menu = Assert.IsAssignableFrom<ContextMenu>(owner.ContextMenu);
        try
        {
            if (owner is MessageFeedRow row)
            {
                var point = MessageBodyPoint(row, window);
                window.MouseMove(point);
                window.MouseDown(point, MouseButton.Right);
                window.MouseUp(point, MouseButton.Right);
            }
            else
            {
                menu.Open(owner);
            }
            Pump(window);
            Assert.True(menu.IsOpen);
            var item = Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, Localization.Get(key)));
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        }
        finally
        {
            menu.Close();
        }
    }

    private static Point MessageBodyPoint(MessageFeedRow row, Window window)
    {
        var body = Assert.Single(row.GetVisualDescendants().OfType<StackPanel>(), control => control.Classes.Contains("messageBody"));
        return body.TranslatePoint(new Point(body.Bounds.Width / 2, body.Bounds.Height / 2), window)!.Value;
    }

    private static Control[] VisibleContainers(ListBox list, ScrollViewer viewer)
    {
        return list.GetRealizedContainers().Where(container => container.IsEffectivelyVisible &&
                container.TranslatePoint(default, viewer) is { } position && position.Y >= 0 &&
                position.Y + container.Bounds.Height <= viewer.Viewport.Height)
            .OrderBy(container => container.TranslatePoint(default, viewer)!.Value.Y).ToArray();
    }

    private static Control TopVisibleNeighbor(ListBox list, ScrollViewer viewer, MessageItemViewModel excluded)
    {
        return list.GetRealizedContainers().Where(container => container.IsEffectivelyVisible &&
                list.ItemFromContainer(container) is MessageItemViewModel actual && !ReferenceEquals(actual, excluded) &&
                container.TranslatePoint(default, viewer) is { } position && position.Y < viewer.Viewport.Height &&
                position.Y + container.Bounds.Height > 0)
            .OrderBy(container => container.TranslatePoint(default, viewer)!.Value.Y).First();
    }

    private static void AssertLayout(ListBox list, ScrollViewer viewer, string stage)
    {
        var containers = list.ItemsPanelRoot!.GetVisualChildren().OfType<MessageFeedListBoxItem>()
            .Where(container => container.IsEffectivelyVisible)
            .OrderBy(container => list.IndexFromContainer(container)).ToArray();
        Assert.Equal(containers.Length, containers.Select(container => container.DataContext).Distinct().Count());
        double? previousBottom = null;
        foreach (var container in containers)
        {
            var item = Assert.IsType<MessageItemViewModel>(container.DataContext);
            Assert.True(list.Items.IndexOf(item) == list.IndexFromContainer(container),
                $"{stage}: {item.Detail} container index={list.IndexFromContainer(container)}, item index={list.Items.IndexOf(item)}");
            Assert.Same(item, list.ItemFromContainer(container));
            var row = Assert.Single(container.GetVisualDescendants().OfType<MessageFeedRow>());
            Assert.Same(item, row.DataContext);
            var rowY = row.TranslatePoint(default, container)!.Value.Y;
            Assert.True(rowY >= -0.5 && rowY + row.Bounds.Height <= container.Bounds.Height + 0.5,
                $"{stage}: {item.Detail} row Y={rowY}, height={row.Bounds.Height}, container height={container.Bounds.Height}");
            var top = container.TranslatePoint(default, viewer)!.Value.Y;
            Assert.True(previousBottom is null || top >= previousBottom.Value - 0.5,
                $"{stage}: {item.Detail} starts at {top}, preceding row ends at {previousBottom}");
            previousBottom = top + container.Bounds.Height;
        }
    }

    private static void AssertReadingPosition(ListBox list, ScrollViewer viewer, MessageItemViewModel item, double before)
    {
        var retained = Assert.IsAssignableFrom<Control>(list.ContainerFromItem(item));
        Assert.InRange(Math.Abs(retained.TranslatePoint(default, viewer)!.Value.Y - before), 0, 1);
    }

    private static void AssertAtLatest(MessageFeedView view, ScrollViewer viewer)
    {
        Assert.False(view.Model.IsReadingHistory);
        Assert.InRange(Math.Abs(viewer.ScrollBarMaximum.Y - viewer.Offset.Y), 0, 1);
    }

    private static ListBox FindList(MessageFeedView view, string name)
    {
        return Assert.Single(view.GetVisualDescendants().OfType<ListBox>(), list => list.Name == name);
    }

    private static ScrollViewer FindViewer(ListBox list)
    {
        return Assert.Single(list.GetVisualDescendants().OfType<ScrollViewer>(), viewer => viewer.TemplatedParent == list);
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
