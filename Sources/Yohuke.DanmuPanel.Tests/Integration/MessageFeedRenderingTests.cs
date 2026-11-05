using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Xunit.Abstractions;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>通过真实消息模板验证滚动复用、当前行交互及单条追加的显示成本。</summary>
public sealed class MessageFeedRenderingTests(ITestOutputHelper output)
{
    /// <summary>容器换绑后复用消息行，按钮和菜单只操作当前消息。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.Danmu, false)]
    [InlineData(MessageKind.Danmu, true)]
    [InlineData(MessageKind.SuperChat, false)]
    [InlineData(MessageKind.SuperChat, true)]
    [InlineData(MessageKind.Gift, false)]
    [InlineData(MessageKind.Guard, true)]
    public async Task ScrollingReusesRowsAndActionsUseCurrentMessage(MessageKind kind, bool useMenu)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new ConfigurationService(directory.Path), new());
        var room = new RoomTabState { Id = "tab", RoomId = "123" };
        await runtime.TestMessages.AddAsync(room, new()
        {
            Kind = kind, UserName = "viewer", Content = "message", GiftName = "gift", Count = 100, UnitPrice = 100
        });
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, kind, errors.Add);
        var window = new Window { Width = 650, Height = 350, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(room);
            window.Show();
            Pump(window);
            var list = FindList(view);
            var viewer = FindViewer(list);
            var selected = view.Model.ScrollingItems[^1];
            list.SelectedItem = selected;
            Pump(window);
            var seen = new Dictionary<Button, MessageItemViewModel>();
            var detachedRows = new HashSet<Button>();
            var peakRealizedRows = 0;
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var elapsed = Stopwatch.StartNew();
            for (var pass = 0; pass < 3; pass++)
            {
                foreach (var fraction in new[] { 0, 0.25, 0.5, 0.75, 1 })
                {
                    var presenter = Assert.Single(viewer.GetVisualDescendants().OfType<ScrollContentPresenter>(),
                presenter => ReferenceEquals(presenter.TemplatedParent, viewer));
                    presenter.RaiseEvent(new ScrollGestureEventArgs(ScrollGestureEventArgs.GetNextFreeId(),
                        new(0, viewer.ScrollBarMaximum.Y * fraction - viewer.Offset.Y)));
                    Pump(window);
                    var rows = Rows(list).ToArray();
                    Assert.Same(selected, list.SelectedItem);
                    Assert.All(list.GetRealizedContainers().OfType<ListBoxItem>(), container =>
                        Assert.Equal(ReferenceEquals(container.DataContext, selected), container.IsSelected));
                    peakRealizedRows = Math.Max(peakRealizedRows, rows.Length);
                    foreach (var row in rows)
                    {
                        var current = (MessageItemViewModel)row.DataContext!;
                        if (seen.TryAdd(row, current))
                        {
                            row.DetachedFromVisualTree += (_, _) => detachedRows.Add(row);
                        }
                    }
                }
            }
            output.WriteLine($"{kind}: 15 scrolls created {seen.Count} distinct rows; {elapsed.Elapsed.TotalMilliseconds:F1} ms; " +
                $"{GC.GetAllocatedBytesForCurrentThread() - allocatedBefore:N0} UI-thread bytes.");
            Assert.InRange(seen.Count, 1, peakRealizedRows * 3);
            var recycled = Rows(list).FirstOrDefault(row => !ReferenceEquals(seen[row], row.DataContext));
            Assert.NotNull(recycled);
            Assert.Contains(recycled, detachedRows);
            var previous = seen[recycled];
            var target = Assert.IsType<MessageItemViewModel>(recycled.DataContext);
            Assert.NotSame(previous, target);
            Assert.Contains(recycled.GetVisualDescendants().OfType<EmoticonTextBlock>(),
                text => text.Classes.Contains("danmuFeedUser") && text.ContentText == target.Name);

            if (useMenu)
            {
                var menu = Assert.IsAssignableFrom<ContextMenu>(recycled.ContextMenu);
                menu.Open(recycled);
                Assert.IsType<MenuItem>(menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                menu.Close();
            }
            else
            {
                recycled.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            await WaitUntilAsync(async () =>
            {
                var updated = (await runtime.Messages.GetByIdAsync(target.Record.Id))!;
                return kind == MessageKind.Danmu ? updated.IsPinned : updated.IsRead;
            });
            var untouched = (await runtime.Messages.GetByIdAsync(previous.Record.Id))!;
            Assert.False(kind == MessageKind.Danmu ? untouched.IsPinned : untouched.IsRead);
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>行移出虚拟化视口时关闭已打开的菜单，避免换绑后误操作其他消息。</summary>
    [AvaloniaFact]
    public async Task RecyclingClosesTheMenuBeforeRebindingItsRow()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new ConfigurationService(directory.Path), new());
        var room = new RoomTabState { Id = "tab", RoomId = "123" };
        await runtime.TestMessages.AddAsync(room, new()
        {
            Kind = MessageKind.Danmu, UserName = "viewer", Content = "message", Count = 100
        });
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, MessageKind.Danmu, errors.Add);
        var window = new Window { Width = 650, Height = 350, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(room);
            window.Show();
            Pump(window);
            var list = FindList(view);
            var row = Rows(list).Last();
            var menu = Assert.IsAssignableFrom<ContextMenu>(row.ContextMenu);
            menu.Open(row);
            Assert.True(menu.IsOpen);

            var viewer = FindViewer(list);
            var presenter = Assert.Single(viewer.GetVisualDescendants().OfType<ScrollContentPresenter>(),
                presenter => ReferenceEquals(presenter.TemplatedParent, viewer));
            presenter.RaiseEvent(new ScrollGestureEventArgs(ScrollGestureEventArgs.GetNextFreeId(), new(0, -viewer.Offset.Y)));
            Pump(window);

            Assert.False(menu.IsOpen);
            Assert.All(view.Model.Items, item => Assert.False(item.Record.IsPinned));
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>底部逐条追加保留可见旧行，不对已实现的新行反复发起定位请求。</summary>
    [AvaloniaFact]
    public async Task SingleAppendsRetainVisibleRowsWithoutRedundantBringIntoView()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new ConfigurationService(directory.Path), new());
        var room = new RoomTabState { Id = "tab", RoomId = "123" };
        await runtime.TestMessages.AddAsync(room, new()
        {
            Kind = MessageKind.Danmu, UserName = "viewer", Content = "message", Count = 100
        });
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, MessageKind.Danmu, errors.Add);
        var window = new Window { Width = 650, Height = 350, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(room);
            window.Show();
            Pump(window);
            var list = FindList(view);
            var viewer = FindViewer(list);
            var requests = 0;
            list.AddHandler(Control.RequestBringIntoViewEvent, (_, _) => requests++, RoutingStrategies.Tunnel, true);
            var elapsed = Stopwatch.StartNew();
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < 12; index++)
            {
                var retained = Rows(list).ToDictionary(row => (MessageItemViewModel)row.DataContext!);
                await runtime.TestMessages.AddAsync(room, new()
                {
                    Kind = MessageKind.Danmu, UserName = "viewer", Content = $"append {index}"
                });
                await view.Model.RefreshAsync();
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
                window.UpdateLayout();
                Assert.InRange(Math.Abs(viewer.ScrollBarMaximum.Y - viewer.Offset.Y), 0, 1);
                var currentRows = Rows(list).ToArray();
                Assert.NotEmpty(currentRows);
                Assert.NotNull(list.ContainerFromItem(view.Model.ScrollingItems[^1]));
                var retainedCount = 0;
                foreach (var row in currentRows)
                {
                    if (retained.TryGetValue((MessageItemViewModel)row.DataContext!, out var original))
                    {
                        Assert.Same(original, row);
                        retainedCount++;
                    }
                }
                Assert.True(retainedCount > 0);
                Assert.Equal(100, view.Model.ScrollingItems.Count);
            }
            output.WriteLine($"12 single appends: {elapsed.Elapsed.TotalMilliseconds:F1} ms; " +
                $"{GC.GetAllocatedBytesForCurrentThread() - allocatedBefore:N0} UI-thread bytes; {requests} bring-into-view requests.");
            Assert.Equal(0, requests);
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    private static IEnumerable<Button> Rows(ListBox list)
    {
        return list.GetVisualDescendants().OfType<Button>().Where(row => row.DataContext is MessageItemViewModel);
    }

    private static ListBox FindList(MessageFeedView view)
    {
        return view.GetVisualDescendants().OfType<ListBox>().Single(list => list.Name == "ScrollingMessages");
    }

    private static ScrollViewer FindViewer(ListBox list)
    {
        return list.GetVisualDescendants().OfType<ScrollViewer>().Single(viewer => viewer.TemplatedParent == list);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (await condition())
            {
                return;
            }
            await Task.Delay(10);
        }
        Assert.Fail("The recycled row did not update the current message.");
    }
}
