using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Layouts;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Views;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证付费消息的阅读标签、停靠迁移及默认未读窗口。</summary>
public sealed class MessageReadTabsTests
{
    /// <summary>已有已读记录时首次打开面板仍只显示未读，全部已读后窗口清空。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.SuperChat)]
    [InlineData(MessageKind.Gift)]
    [InlineData(MessageKind.Guard)]
    public async Task FeedDefaultsToUnreadAndMarkAllReadEmptiesIt(MessageKind kind)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        await runtime.Messages.AppendBatchAsync([Message(kind, 0), Message(kind, 1)], new());
        await runtime.Messages.MarkReadAsync("message-0", 1, 30);
        var view = new MessageFeedView(runtime, kind, exception => Assert.Fail(exception.ToString()));
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "tab" });

            Assert.Equal("message-1", Assert.Single(view.Model.Items).Record.Id);
            Assert.Equal(1, view.Model.UnreadCount);
            await view.Model.MarkAllReadAsync();
            Assert.Empty(view.Model.Items);
            Assert.Empty(view.Model.ScrollingRows);
            Assert.Equal(0, view.Model.UnreadCount);
        }
        finally
        {
            view.Model.Dispose();
        }
    }

    /// <summary>标题右侧的标签支持鼠标切换，浮动后重建控件并保留所选标签和未读计数。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.SuperChat, WorkbenchPanelIds.SUPER_CHAT)]
    [InlineData(MessageKind.Gift, WorkbenchPanelIds.GIFT)]
    [InlineData(MessageKind.Guard, WorkbenchPanelIds.GUARD)]
    public async Task HeaderTabsSwitchListsAndKeepSelectionWhenFloating(MessageKind kind, string panelId)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        await runtime.Messages.AppendBatchAsync([Message(kind, 0), Message(kind, 1)], new());
        await runtime.Messages.MarkReadAsync("message-0", 1, 30);
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, kind, errors.Add);
        var shell = await CreateShellAsync(directory, view, panelId);
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "tab" });
            shell.Show();
            Pump(shell);
            var tabs = FindTabs(shell);
            Assert.Equal(0, tabs.SelectedIndex);
            var unreadTab = Assert.IsType<TabStripItem>(tabs.Items[0]);
            var readTab = Assert.IsType<TabStripItem>(tabs.Items[1]);
            Assert.Equal(Localization.Get("Panel.Unread"), unreadTab.Content);
            Assert.Equal(Localization.Get("Panel.Read"), readTab.Content);
            var title = Assert.Single(shell.GetVisualDescendants().OfType<TextBlock>(), text => text.Name == "PART_Title");
            Assert.True(tabs.TranslatePoint(default, title)!.Value.X >= title.Bounds.Width);

            Click(shell, readTab);
            await WaitUntilAsync(shell, () => view.Model.Items.Count == 1 && view.Model.Items[0].Record.IsRead);
            Assert.Equal("message-0", view.Model.Items[0].Record.Id);
            Assert.Equal(1, tabs.SelectedIndex);
            Assert.Equal(1, view.Model.UnreadCount);

            shell.FloatPanel(panelId);
            Pump(shell);
            var floating = Assert.Single(shell.Layouts.FloatingWindows);
            Pump(floating);
            var floatingTabs = FindTabs(floating);
            Assert.NotSame(tabs, floatingTabs);
            Assert.Equal(1, floatingTabs.SelectedIndex);
            await runtime.Messages.AppendAsync(Message(kind, 2), new());
            await view.Model.RefreshAsync();
            Pump(floating);
            Assert.Equal("message-0", Assert.Single(view.Model.Items).Record.Id);
            Assert.Equal(2, view.Model.UnreadCount);

            Click(floating, Assert.IsType<TabStripItem>(floatingTabs.Items[0]));
            await WaitUntilAsync(floating, () => view.Model.Items.Count == 2 && view.Model.Items.All(item => !item.Record.IsRead));
            Assert.Equal(0, floatingTabs.SelectedIndex);
            Assert.Equal(new[] { "message-1", "message-2" }, view.Model.Items.Select(item => item.Record.Id));
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            await shell.CloseAsync();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>普通弹幕标题保持原有布局。</summary>
    [AvaloniaFact]
    public async Task DanmuHeaderHasNoReadTabs()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        var view = new MessageFeedView(runtime, MessageKind.Danmu, exception => Assert.Fail(exception.ToString()));
        var shell = await CreateShellAsync(directory, view, WorkbenchPanelIds.DANMU);
        try
        {
            shell.Show();
            Pump(shell);
            Assert.DoesNotContain(shell.GetVisualDescendants().OfType<TabStrip>(), tabs => tabs.Name == "MessageReadTabs");
        }
        finally
        {
            view.Model.Dispose();
            await shell.CloseAsync();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>普通和紧凑行支持点击或菜单确认，确认后在已读标签可查看且重复点击不再写入。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.SuperChat, false, false)]
    [InlineData(MessageKind.SuperChat, true, false)]
    [InlineData(MessageKind.Gift, false, false)]
    [InlineData(MessageKind.Gift, true, false)]
    [InlineData(MessageKind.Guard, false, false)]
    [InlineData(MessageKind.Guard, true, false)]
    [InlineData(MessageKind.SuperChat, false, true)]
    [InlineData(MessageKind.SuperChat, true, true)]
    [InlineData(MessageKind.Gift, false, true)]
    [InlineData(MessageKind.Gift, true, true)]
    [InlineData(MessageKind.Guard, false, true)]
    [InlineData(MessageKind.Guard, true, true)]
    public async Task ClickingUnreadMovesToReadAndLeavesReadRowsUnchanged(MessageKind kind, bool compact, bool useContextMenu)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        runtime.Configuration.Settings.CompactDanmuDisplay = compact;
        await runtime.Messages.AppendAsync(Message(kind, 0), new());
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, kind, errors.Add);
        var window = new Window { Width = 500, Height = 360, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "tab" });
            window.Show();
            Pump(window);
            var row = Assert.Single(view.GetVisualDescendants().OfType<MessageFeedRow>());
            if (useContextMenu)
            {
                var menu = Assert.IsType<LocalizedContextMenu>(row.ContextMenu);
                menu.Open(row);
                Pump(window);
                var markRead = Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, Localization.Get("Panel.MarkRead")));
                markRead.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                menu.Close();
            }
            else
            {
                Click(window, row);
            }
            await WaitUntilAsync(window, () => view.Model.Items.Count == 0);
            Assert.Empty(view.Model.ScrollingRows);
            Assert.Equal(0, view.Model.UnreadCount);
            Assert.True((await runtime.Messages.GetByIdAsync("message-0"))!.IsRead);

            await view.Model.SelectReadFilterAsync(MessageReadFilter.Read);
            Pump(window);
            var item = Assert.Single(view.Model.Items);
            Assert.True(item.Record.IsRead);
            row = Assert.Single(view.GetVisualDescendants().OfType<MessageFeedRow>());
            var record = item.Record;
            Click(window, row);
            await view.Model.RefreshAsync();
            Pump(window);
            Assert.Same(item, Assert.Single(view.Model.Items));
            Assert.Same(record, item.Record);
            var readMenu = Assert.IsType<LocalizedContextMenu>(row.ContextMenu);
            readMenu.Open(row);
            var readAction = Assert.Single(readMenu.Items.OfType<MenuItem>(), entry => Equals(entry.Header, Localization.Get("Panel.MarkRead")));
            Assert.False(readAction.IsEnabled);
            readMenu.Close();
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>语言切换即时更新标签文案并保留选中状态。</summary>
    [AvaloniaFact]
    public async Task ReadTabsFollowLanguageChangesWithoutResettingSelection()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        var view = new MessageFeedView(runtime, MessageKind.SuperChat, exception => Assert.Fail(exception.ToString()));
        var language = Localization.SelectedLanguageID;
        var tabs = Assert.IsType<TabStrip>(view.HeaderTabsTemplate!.Build(view));
        try
        {
            await view.Model.SelectReadFilterAsync(MessageReadFilter.Read);
            foreach (var current in new[] { "zh-CN", "en-US", "ja-JP" })
            {
                Localization.SetLanguage(current);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(Localization.Get("Panel.Unread"), Assert.IsType<TabStripItem>(tabs.Items[0]).Content);
                Assert.Equal(Localization.Get("Panel.Read"), Assert.IsType<TabStripItem>(tabs.Items[1]).Content);
                Assert.Equal(1, tabs.SelectedIndex);
            }
        }
        finally
        {
            view.Model.Dispose();
            Localization.SetLanguage(language);
        }
    }

    private static async Task<ShellWindow> CreateShellAsync(MessageStoreTestDirectory directory, MessageFeedView view, string panelId)
    {
        var panels = WorkbenchPanelIds.All.ToDictionary(id => id, id => (Control)new TextBlock { Text = id });
        panels[panelId] = view;
        await new WorkspaceLayoutStore(directory.Path).SaveAsync(new()
        {
            CurrentLayout = new()
            {
                Main = new() { PanelIds = [panelId], ActivePanelId = panelId },
                HiddenPanelIds = WorkbenchPanelIds.All.Where(id => id != panelId).ToList(),
                FocusedPanelId = panelId
            }
        });
        var shell = new ShellWindow(panels, new TextBlock(), directory.Path);
        await shell.InitializeLayoutAsync();
        return shell;
    }

    private static PanelRuntime CreateRuntime(MessageStoreTestDirectory directory)
    {
        var configuration = new PanelConfiguration();
        configuration.Settings.Translation.Enabled = false;
        return new(new ConfigurationService(directory.Path), configuration);
    }

    private static MessageRecord Message(MessageKind kind, int index)
    {
        return new()
        {
            Id = "message-" + index, RoomTabId = "tab", RoomId = "123", Kind = kind,
            UserName = "viewer", Content = "谢谢支持", GiftName = "鲜花", GuardLevel = 3,
            UnitPrice = 30, TotalPrice = 30, Time = DateTimeOffset.FromUnixTimeSeconds(1800000000 + index)
        };
    }

    private static TabStrip FindTabs(Window window)
    {
        return Assert.Single(window.GetVisualDescendants().OfType<TabStrip>(), tabs => tabs.Name == "MessageReadTabs");
    }

    private static void Click(Window window, Control control)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
    }

    private static async Task WaitUntilAsync(Window window, Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            Pump(window);
            if (condition())
            {
                return;
            }
            await Task.Delay(10);
        }
        Assert.Fail("The selected read tab did not finish updating its message list.");
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
