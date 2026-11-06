using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Dock.Avalonia.Controls;
using Yohuke.DanmuPanel.Layouts;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证未读角标不占用消息布局，标题计数跟随房间和阅读状态。</summary>
public sealed class UnreadIndicatorTests
{
    /// <summary>弹幕 pin 叠加在左上角，显示或隐藏时不挤占用户信息和正文。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PinBadgeOverlaysTopLeftWithoutChangingLayout(bool compact)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        runtime.Configuration.Settings.CompactDanmuDisplay = compact;
        var message = Message(MessageKind.Danmu) with { MedalName = "粉丝牌", MedalLevel = 10 };
        await runtime.Messages.AppendAsync(message, new());
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, MessageKind.Danmu, errors.Add);
        var window = new Window { Width = 480, Height = 360, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "tab" });
            window.Show();
            Pump(window);
            var item = Assert.Single(view.Model.Items);
            var row = FindRow(view, message.Id);
            var pin = Assert.Single(row.GetVisualDescendants().OfType<TextBlock>(), control => control.Classes.Contains("messagePinBadge"));
            var identity = Assert.Single(row.GetVisualDescendants().OfType<DockPanel>(), control => control.Classes.Contains("danmuFeedIdentity"));
            var body = Assert.Single(row.GetVisualDescendants().OfType<Panel>(), control => control.Classes.Contains("messageBody"));
            Assert.False(pin.IsVisible);
            var rowSize = row.Bounds.Size;
            var identityBounds = identity.Children.Select(child => child.Bounds).ToArray();
            var bodyBounds = body.Bounds;

            item.Update(item.Record with { IsPinned = true }, runtime.Configuration.Settings);
            Pump(window);

            Assert.True(pin.IsEffectivelyVisible);
            Assert.False(pin.IsHitTestVisible);
            Assert.Equal("📌", pin.Text);
            var position = pin.TranslatePoint(default, row)!.Value;
            Assert.InRange(position.X, 0, 8);
            Assert.InRange(position.Y, 0, 6);
            Assert.Equal(default, Assert.IsType<Canvas>(pin.Parent).DesiredSize);
            Assert.DoesNotContain(identity.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "📌");
            Assert.Equal(rowSize, row.Bounds.Size);
            Assert.Equal(identityBounds, identity.Children.Select(child => child.Bounds));
            Assert.Equal(bodyBounds, body.Bounds);

            item.Update(item.Record with { IsPinned = false }, runtime.Configuration.Settings);
            Pump(window);

            Assert.False(pin.IsVisible);
            Assert.Equal(rowSize, row.Bounds.Size);
            Assert.Equal(identityBounds, identity.Children.Select(child => child.Bounds));
            Assert.Equal(bodyBounds, body.Bounds);
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>普通与紧凑行的红点位于右上角，隐藏后内容尺寸与位置不变。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.SuperChat, false)]
    [InlineData(MessageKind.SuperChat, true)]
    [InlineData(MessageKind.Gift, false)]
    [InlineData(MessageKind.Gift, true)]
    [InlineData(MessageKind.Guard, false)]
    [InlineData(MessageKind.Guard, true)]
    public async Task UnreadDotOverlaysRowWithoutChangingLayout(MessageKind kind, bool compact)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        runtime.Configuration.Settings.CompactDanmuDisplay = compact;
        var message = Message(kind);
        await runtime.Messages.AppendAsync(message, new());
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, kind, errors.Add);
        var window = new Window { Width = 480, Height = 360, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "tab" });
            window.Show();
            Pump(window);
            var item = Assert.Single(view.Model.Items);
            var row = FindRow(view, message.Id);
            var card = Assert.Single(row.GetVisualDescendants().OfType<Border>(), control => control.Classes.Contains("messageCard"));
            Assert.Equal(new Thickness(2), card.BorderThickness);
            Assert.Equal(new CornerRadius(7), card.CornerRadius);
            var dot = Assert.Single(row.GetVisualDescendants().OfType<Border>(), control => control.Classes.Contains("messageUnreadDot"));
            var identity = Assert.Single(row.GetVisualDescendants().OfType<DockPanel>(), control => control.Classes.Contains("danmuFeedIdentity"));
            var body = Assert.Single(row.GetVisualDescendants().OfType<Panel>(), control => control.Classes.Contains("messageBody"));
            Assert.True(dot.IsEffectivelyVisible);
            Assert.False(dot.IsHitTestVisible);
            Assert.Equal(Color.Parse("#EF4444"), Assert.IsAssignableFrom<ISolidColorBrush>(dot.Background).Color);
            var position = dot.TranslatePoint(default, row)!.Value;
            Assert.InRange(position.X, row.Bounds.Width - 16, row.Bounds.Width - dot.Bounds.Width);
            Assert.InRange(position.Y, 0, 6);
            Assert.DoesNotContain(row.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "●");
            var rowSize = row.Bounds.Size;
            var identityBounds = identity.Bounds;
            var bodyBounds = body.Bounds;

            item.Update(item.Record with { IsRead = true }, runtime.Configuration.Settings);
            Pump(window);

            Assert.False(dot.IsVisible);
            Assert.Equal(rowSize, row.Bounds.Size);
            Assert.Equal(identityBounds, identity.Bounds);
            Assert.Equal(bodyBounds, body.Bounds);
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>计数覆盖超过一页的未读记录，点击、浮动、切房和清空都会更新标题。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.SuperChat, WorkbenchPanelIds.SUPER_CHAT)]
    [InlineData(MessageKind.Gift, WorkbenchPanelIds.GIFT)]
    [InlineData(MessageKind.Guard, WorkbenchPanelIds.GUARD)]
    public async Task TitleBadgeTracksUnreadRecordsAcrossHostsAndRooms(MessageKind kind, string panelId)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        var options = new MessageReadOptions { GiftAutoPinMinimum = 50 };
        runtime.Configuration.Settings.Reading = options;
        for (var index = 0; index < 105; index++)
        {
            await runtime.Messages.AppendAsync(Message(kind) with { Quantity = 3, UnitPrice = 50, TotalPrice = 150 }, options);
        }
        await runtime.Messages.AppendAsync(Message(kind) with { RoomTabId = "second", UnitPrice = 50, TotalPrice = 50 }, options);
        var hiddenGift = Message(MessageKind.Gift) with { UnitPrice = 1, TotalPrice = 1 };
        if (kind == MessageKind.Gift)
        {
            await runtime.Messages.AppendAsync(hiddenGift, options);
        }
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, kind, errors.Add);
        var panels = WorkbenchPanelIds.All.ToDictionary(id => id, id => (Control)new TextBlock { Text = id });
        panels[panelId] = view;
        var shell = new ShellWindow(panels, new TextBlock(), directory.Path);
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "tab" });
            await shell.InitializeLayoutAsync();
            shell.Show();
            Pump(shell);
            var badge = FindBadge(shell);
            AssertBadge(badge, "105");
            var title = Assert.Single(badge.GetVisualAncestors().OfType<Grid>().First().GetVisualDescendants().OfType<TextBlock>(), text => text.Name == "PART_Title");
            Assert.True(badge.TranslatePoint(default, title)!.Value.X >= title.Bounds.Width);
            var first = view.Model.ScrollingItems[^1];
            FindRow(view, first.Record.Id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (var attempt = 0; attempt < 100 && !first.Record.IsRead; attempt++)
            {
                await Task.Delay(10);
                Dispatcher.UIThread.RunJobs();
            }
            Assert.True(first.Record.IsRead);
            Pump(shell);
            AssertBadge(badge, "104");

            shell.FloatPanel(panelId);
            Pump(shell);
            var floating = Assert.Single(shell.Layouts.FloatingWindows);
            Pump(floating);
            var floatingBadge = FindBadge(floating);
            Assert.NotSame(badge, floatingBadge);
            AssertBadge(floatingBadge, "104");

            await view.Model.SelectRoomAsync(new() { Id = "second" });
            Pump(floating);
            AssertBadge(floatingBadge, "1");
            await runtime.Messages.ClearRoomAsync("second");
            await view.Model.RefreshAsync();
            Pump(floating);
            Assert.False(floatingBadge.IsVisible);

            await view.Model.SelectRoomAsync(new() { Id = "tab" });
            Pump(floating);
            AssertBadge(floatingBadge, "104");
            var markAllRead = Assert.Single(floating.GetVisualDescendants().OfType<Button>(), button => button.Name == "MarkAllReadButton");
            if (kind == MessageKind.Gift)
            {
                runtime.Configuration.Settings.Reading = options with { GiftAutoPinMinimum = 200 };
                await runtime.ApplySettingsAsync();
                await view.Model.RefreshAsync();
                Pump(floating);
                Assert.False(floatingBadge.IsVisible);
                Assert.False(markAllRead.IsEffectivelyEnabled);

                runtime.Configuration.Settings.Reading = options with { GiftAutoPinMinimum = 1 };
                await runtime.ApplySettingsAsync();
                await view.Model.RefreshAsync();
                Pump(floating);
                AssertBadge(floatingBadge, "105");

                runtime.Configuration.Settings.Reading = options;
                await runtime.ApplySettingsAsync();
                await view.Model.RefreshAsync();
                Pump(floating);
                AssertBadge(floatingBadge, "104");
                Assert.False((await runtime.Messages.GetByIdAsync(hiddenGift.Id))!.IsRead);
            }
            Assert.True(markAllRead.IsEffectivelyEnabled);
            await Assert.IsAssignableFrom<IAsyncRelayCommand>(markAllRead.Command).ExecuteAsync(null);
            Pump(floating);
            Assert.False(floatingBadge.IsVisible);
            Assert.False(markAllRead.IsEffectivelyEnabled);
            Assert.All(await runtime.Messages.LoadPageAsync("tab", kind, 0, 200), record => Assert.True(record.IsRead));

            await runtime.Messages.AppendAsync(Message(kind) with { UnitPrice = 50, TotalPrice = 50 }, options);
            await view.Model.RefreshAsync();
            Pump(floating);
            AssertBadge(floatingBadge, "1");
            Assert.True(markAllRead.IsEffectivelyEnabled);
            await view.Model.SelectRoomAsync(null);
            Pump(floating);
            Assert.False(floatingBadge.IsVisible);
            Assert.False(markAllRead.IsEffectivelyEnabled);
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            await shell.CloseAsync();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>未选中标签显示未读计数，激活后与标题同步，并保持徽标文字颜色。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.SuperChat, WorkbenchPanelIds.SUPER_CHAT)]
    [InlineData(MessageKind.Gift, WorkbenchPanelIds.GIFT)]
    [InlineData(MessageKind.Guard, WorkbenchPanelIds.GUARD)]
    public async Task TabBadgeTracksUnreadWhilePanelIsInactive(MessageKind kind, string panelId)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        await runtime.Messages.AppendAsync(Message(kind), new());
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, kind, errors.Add);
        var panels = WorkbenchPanelIds.All.ToDictionary(id => id, id => (Control)new TextBlock { Text = id });
        panels[panelId] = view;
        await new WorkspaceLayoutStore(directory.Path).SaveAsync(new()
        {
            CurrentLayout = new()
            {
                Main = new() { PanelIds = [WorkbenchPanelIds.DANMU, panelId], ActivePanelId = WorkbenchPanelIds.DANMU },
                HiddenPanelIds = WorkbenchPanelIds.All.Where(id => id != WorkbenchPanelIds.DANMU && id != panelId).ToList(),
                FocusedPanelId = WorkbenchPanelIds.DANMU
            }
        });
        var shell = new ShellWindow(panels, new TextBlock(), directory.Path);
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "tab" });
            await shell.InitializeLayoutAsync();
            shell.Show();
            Pump(shell);
            var tab = Assert.Single(shell.GetVisualDescendants().OfType<ToolTabStripItem>(),
                item => ReferenceEquals(item.DataContext, shell.Layouts.PanelAdapters[panelId]));
            var danmuTab = Assert.Single(shell.GetVisualDescendants().OfType<ToolTabStripItem>(),
                item => ReferenceEquals(item.DataContext, shell.Layouts.PanelAdapters[WorkbenchPanelIds.DANMU]));
            Assert.DoesNotContain(danmuTab.GetVisualDescendants().OfType<Border>(),
                control => control.Classes.Contains("panelUnreadBadge"));
            Assert.False(tab.IsSelected);
            var badge = Assert.Single(tab.GetVisualDescendants().OfType<Border>(),
                control => control.Classes.Contains("panelUnreadBadge"));
            AssertBadge(badge, "1");
            var count = Assert.IsType<TextBlock>(badge.Child);
            Assert.Equal(Colors.White, Assert.IsAssignableFrom<ISolidColorBrush>(count.Foreground).Color);
            var title = Assert.Single(tab.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == shell.Layouts.PanelAdapters[panelId].Title);
            Assert.True(badge.TranslatePoint(default, title)!.Value.X >= title.Bounds.Width);

            await runtime.Messages.AppendAsync(Message(kind), new());
            await view.Model.RefreshAsync();
            Pump(shell);
            AssertBadge(badge, "2");

            shell.Layouts.ShowPanel(panelId);
            Pump(shell);
            Assert.True(tab.IsSelected);
            AssertBadge(badge, "2");
            AssertBadge(FindBadge(shell), "2");
            Assert.Equal(Colors.White, Assert.IsAssignableFrom<ISolidColorBrush>(count.Foreground).Color);

            shell.Layouts.ShowPanel(WorkbenchPanelIds.DANMU);
            Pump(shell);
            Assert.False(tab.IsSelected);
            await view.Model.MarkAllReadAsync();
            Pump(shell);
            Assert.False(badge.IsVisible);
            Assert.Equal(0, badge.DesiredSize.Width);

            await runtime.Messages.AppendAsync(Message(kind) with { RoomTabId = "second" }, new());
            await view.Model.SelectRoomAsync(new() { Id = "second" });
            Pump(shell);
            AssertBadge(badge, "1");
            await view.Model.SelectRoomAsync(null);
            Pump(shell);
            Assert.False(badge.IsVisible);
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            await shell.CloseAsync();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>全部已读期间切换房间，只确认原房间，新的房间保留自身未读计数。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.SuperChat)]
    [InlineData(MessageKind.Gift)]
    [InlineData(MessageKind.Guard)]
    public async Task SwitchingRoomDuringMarkAllReadKeepsNewRoomUnread(MessageKind kind)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        await runtime.Messages.AppendAsync(Message(kind), new());
        await runtime.Messages.AppendAsync(Message(kind) with { RoomTabId = "second" }, new());
        await runtime.Messages.AppendAsync(Message(kind) with { RoomTabId = "second" }, new());
        using var model = new FeedViewModel(runtime, kind);
        await model.SelectRoomAsync(new() { Id = "tab" });
        var marking = model.MarkAllReadAsync();
        await model.SelectRoomAsync(new() { Id = "second" });
        await marking;

        Assert.Equal(2, model.UnreadCount);
        Assert.All(model.Items, item => Assert.Equal("second", item.Record.RoomTabId));
        Assert.All(model.Items, item => Assert.False(item.Record.IsRead));
        Assert.True(Assert.Single(await runtime.Messages.LoadPageAsync("tab", kind, 0, 10)).IsRead);
    }

    private static PanelRuntime CreateRuntime(MessageStoreTestDirectory directory)
    {
        var configuration = new PanelConfiguration();
        configuration.Settings.Translation.Enabled = false;
        return new(new ConfigurationService(directory.Path), configuration);
    }

    private static MessageRecord Message(MessageKind kind)
    {
        return new() { RoomTabId = "tab", RoomId = "123", Kind = kind, UserName = "Viewer", Content = "Thank you", GuardLevel = 3 };
    }

    private static Button FindRow(Control view, string id)
    {
        return view.GetVisualDescendants().OfType<Button>()
            .First(button => button.DataContext is MessageItemViewModel item && item.Record.Id == id);
    }

    private static Border FindBadge(Window window)
    {
        return Assert.Single(window.GetVisualDescendants().OfType<Border>(), control =>
            control.Classes.Contains("panelUnreadBadge") && !control.GetVisualAncestors().OfType<ToolTabStripItem>().Any());
    }

    private static void AssertBadge(Border badge, string count)
    {
        Assert.True(badge.IsEffectivelyVisible);
        Assert.Equal(count, Assert.IsType<TextBlock>(badge.Child).Text);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
