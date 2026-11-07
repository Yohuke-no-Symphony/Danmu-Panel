using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Lucide.Avalonia;
using Xunit;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Views;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证付费消息面板从标题栏或菜单回到顶部，并保持未读消息的阅读位置。</summary>
public sealed class MessageFeedTopNavigationTests
{
    /// <summary>回顶取消尚未执行的回底任务，刷新后首条未读仍可见且阅读状态不变。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.SuperChat, false)]
    [InlineData(MessageKind.Gift, false)]
    [InlineData(MessageKind.Guard, false)]
    [InlineData(MessageKind.SuperChat, true)]
    [InlineData(MessageKind.Gift, true)]
    [InlineData(MessageKind.Guard, true)]
    public async Task TopNavigationShowsUnreadAndSurvivesRefresh(MessageKind kind, bool useContextMenu)
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = new PanelConfiguration();
        configuration.Settings.Translation.Enabled = false;
        await using var runtime = new PanelRuntime(new(directory.Path), configuration);
        var messages = Enumerable.Range(0, 60).Select(index => Message(kind, index)).ToArray();
        await runtime.Messages.AppendBatchAsync(messages, new());
        var unread = messages[0];
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, kind, errors.Add);
        var window = new Window { Width = 520, Height = 320, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "tab", RoomId = "123" });
            window.Show();
            await view.Model.WaitForPresentationAsync();
            Pump(window);
            var list = Assert.Single(view.GetVisualDescendants().OfType<ListBox>(), control => control.Name == "ScrollingMessages");
            var viewer = Assert.Single(list.GetVisualDescendants().OfType<ScrollViewer>(), control => control.TemplatedParent == list);
            Assert.InRange(viewer.Offset.Y, 0, 1);
            var actions = Assert.IsType<StackPanel>(view.HeaderActionsTemplate.Build(view));
            var latest = actions.Children.OfType<Button>().Single(button => button.Name == "BackToLatestButton");
            await Assert.IsAssignableFrom<IAsyncRelayCommand>(latest.Command).ExecuteAsync(null);

            if (useContextMenu)
            {
                var menu = Assert.IsType<LocalizedContextMenu>(list.ContextMenu);
                var top = Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, Localization.Get("Panel.BackToTop")));
                top.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            }
            else
            {
                var top = actions.Children.OfType<Button>().Single(button => button.Name == "BackToTopButton");
                await Assert.IsAssignableFrom<IAsyncRelayCommand>(top.Command).ExecuteAsync(null);
            }
            Pump(window);

            Assert.InRange(viewer.Offset.Y, 0, 1);
            Assert.True(view.Model.IsReadingHistory);
            var first = view.Model.ScrollingItems[0];
            Assert.Equal(unread.Id, first.Record.Id);
            var container = Assert.IsAssignableFrom<Control>(list.ContainerFromItem(first));
            Assert.InRange(Math.Abs(container.TranslatePoint(default, viewer)!.Value.Y), 0, 1);
            Assert.True(first.ShowUnreadDot);
            Assert.Equal(60, view.Model.UnreadCount);

            await runtime.Messages.AppendAsync(Message(kind, 61), new());
            await view.Model.RefreshAsync();
            await view.Model.WaitForPresentationAsync();
            Pump(window);

            Assert.InRange(viewer.Offset.Y, 0, 1);
            Assert.Same(first, view.Model.ScrollingItems[0]);
            Assert.False(first.Record.IsRead);
            Assert.Equal(61, view.Model.UnreadCount);
            await Assert.IsAssignableFrom<IAsyncRelayCommand>(latest.Command).ExecuteAsync(null);
            Pump(window);
            Assert.InRange(Math.Abs(viewer.ScrollBarMaximum.Y - viewer.Offset.Y), 0, 1);
            Assert.False(view.Model.IsReadingHistory);
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>回顶按钮限定三类面板，独立创建且提示和无障碍名称随语言更新。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.Danmu)]
    [InlineData(MessageKind.SuperChat)]
    [InlineData(MessageKind.Gift)]
    [InlineData(MessageKind.Guard)]
    public async Task HeaderTopActionsAreIndependentAndLocalized(MessageKind kind)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, kind, errors.Add);
        var originalLanguage = Localization.SelectedLanguageID;
        try
        {
            var firstActions = Assert.IsType<StackPanel>(view.HeaderActionsTemplate.Build(view));
            var secondActions = Assert.IsType<StackPanel>(view.HeaderActionsTemplate.Build(view));
            if (kind == MessageKind.Danmu)
            {
                Assert.DoesNotContain(firstActions.Children.OfType<Button>(), button => button.Name == "BackToTopButton");
                return;
            }
            var first = Assert.Single(firstActions.Children.OfType<Button>(), button => button.Name == "BackToTopButton");
            var second = Assert.Single(secondActions.Children.OfType<Button>(), button => button.Name == "BackToTopButton");
            Assert.NotSame(first, second);
            Assert.False(first.IsEnabled);
            await view.Model.SelectRoomAsync(new() { Id = "tab", RoomId = "123" });
            Assert.True(first.IsEnabled);
            await Assert.IsAssignableFrom<IAsyncRelayCommand>(first.Command).ExecuteAsync(null);
            Assert.True(view.Model.IsReadingHistory);
            foreach (var language in new[] { "zh-CN", "en-US", "ja-JP" })
            {
                Localization.SetLanguage(language);
                Assert.Equal(Localization.Get("Panel.BackToTop"), ToolTip.GetTip(first));
                Assert.Equal(Localization.Get("Panel.BackToTop"), AutomationProperties.GetName(first));
                Assert.Equal(LucideIconKind.ArrowUpToLine, Assert.IsType<LucideIcon>(first.Content).Kind);
            }
            var latest = firstActions.Children.OfType<Button>().Single(button => button.Name == "BackToLatestButton");
            await Assert.IsAssignableFrom<IAsyncRelayCommand>(latest.Command).ExecuteAsync(null);
            Assert.False(view.Model.IsReadingHistory);
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            Localization.SetLanguage(originalLanguage);
        }
    }

    /// <summary>空面板和尚未附加到窗口的面板也会记住回顶，首批消息到达后不回到底部。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.SuperChat, false)]
    [InlineData(MessageKind.Gift, false)]
    [InlineData(MessageKind.Guard, false)]
    [InlineData(MessageKind.SuperChat, true)]
    [InlineData(MessageKind.Gift, true)]
    [InlineData(MessageKind.Guard, true)]
    public async Task EmptyFeedRemembersTopNavigationUntilMessagesArrive(MessageKind kind, bool attachBeforeClick)
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = new PanelConfiguration();
        configuration.Settings.Translation.Enabled = false;
        await using var runtime = new PanelRuntime(new(directory.Path), configuration);
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, kind, errors.Add);
        var window = new Window { Width = 520, Height = 320, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "tab", RoomId = "123" });
            if (attachBeforeClick)
            {
                window.Show();
                Pump(window);
            }
            var actions = Assert.IsType<StackPanel>(view.HeaderActionsTemplate.Build(view));
            var top = actions.Children.OfType<Button>().Single(button => button.Name == "BackToTopButton");
            await Assert.IsAssignableFrom<IAsyncRelayCommand>(top.Command).ExecuteAsync(null);
            Assert.True(view.Model.IsReadingHistory);
            if (!attachBeforeClick)
            {
                window.Show();
            }
            Pump(window);

            await runtime.Messages.AppendBatchAsync(Enumerable.Range(0, 60).Select(index => Message(kind, index)).ToArray(), new());
            await view.Model.RefreshAsync();
            await view.Model.WaitForPresentationAsync();
            Pump(window);

            var list = Assert.Single(view.GetVisualDescendants().OfType<ListBox>(), control => control.Name == "ScrollingMessages");
            var viewer = Assert.Single(list.GetVisualDescendants().OfType<ScrollViewer>(), control => control.TemplatedParent == list);
            Assert.True(viewer.ScrollBarMaximum.Y > 0);
            Assert.InRange(viewer.Offset.Y, 0, 1);
            Assert.True(view.Model.IsReadingHistory);
            Assert.NotNull(list.ContainerFromItem(view.Model.ScrollingItems[0]));
            Assert.Equal(60, view.Model.UnreadCount);
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    private static MessageRecord Message(MessageKind kind, int sequence)
    {
        return new()
        {
            Id = "message-" + sequence, RoomTabId = "tab", RoomId = "123", Kind = kind,
            UserName = "viewer", Content = "消息 " + sequence, GiftId = "flower", GiftName = "鲜花",
            UnitPrice = 100, TotalPrice = 100, GuardLevel = 3,
            Time = DateTimeOffset.FromUnixTimeSeconds(1800000000 + sequence)
        };
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
