using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Layouts;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Views;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>通过真实标题按钮验证四类面板的清空确认、浮窗归属和房间隔离。</summary>
public sealed class ClearFeedInteractionTests
{
    /// <summary>停靠和浮动标题均需确认后才清空，取消或关闭弹窗保留数据。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.Danmu, WorkbenchPanelIds.DANMU, false)]
    [InlineData(MessageKind.SuperChat, WorkbenchPanelIds.SUPER_CHAT, false)]
    [InlineData(MessageKind.Gift, WorkbenchPanelIds.GIFT, false)]
    [InlineData(MessageKind.Guard, WorkbenchPanelIds.GUARD, false)]
    [InlineData(MessageKind.Danmu, WorkbenchPanelIds.DANMU, true)]
    [InlineData(MessageKind.SuperChat, WorkbenchPanelIds.SUPER_CHAT, true)]
    [InlineData(MessageKind.Gift, WorkbenchPanelIds.GIFT, true)]
    [InlineData(MessageKind.Guard, WorkbenchPanelIds.GUARD, true)]
    public async Task HeaderClearRequiresConfirmationAndOnlyClearsItsFeed(MessageKind kind, string panelId, bool floating)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var room = new RoomTabState { Id = "tab", Name = "目标房间" };
        foreach (var other in Enum.GetValues<MessageKind>())
        {
            await runtime.Messages.AppendAsync(Message(other), new());
        }
        await runtime.Messages.AppendAsync(Message(kind) with { RoomTabId = "second" }, new());
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, kind, errors.Add);
        var panels = WorkbenchPanelIds.All.ToDictionary(id => id, id => (Control)new TextBlock { Text = id });
        panels[panelId] = view;
        var shell = new ShellWindow(panels, new TextBlock(), directory.Path);
        try
        {
            await shell.InitializeLayoutAsync();
            shell.Show();
            Pump(shell);
            var dockedButton = FindClearButton(shell);
            Assert.False(dockedButton.IsEffectivelyEnabled);
            await view.Model.SelectRoomAsync(room);
            Pump(shell);
            Window owner = shell;
            if (floating)
            {
                shell.FloatPanel(panelId);
                Pump(shell);
                owner = Assert.Single(shell.Layouts.FloatingWindows);
                Pump(owner);
                Assert.NotSame(dockedButton, FindClearButton(owner));
            }
            var button = FindClearButton(owner);
            Assert.True(button.IsEffectivelyEnabled);
            var command = Assert.IsAssignableFrom<IAsyncRelayCommand>(button.Command);
            var cancelling = command.ExecuteAsync(null);
            var dialog = Assert.Single(owner.OwnedWindows.OfType<ConfirmationWindow>());
            Assert.Contains(room.Name, dialog.DeleteDialogDescription);
            Assert.Contains(Localization.Get("Workbench.Panel." + kind), dialog.DeleteDialogDescription);
            Assert.Single(await runtime.Messages.LoadPageAsync("tab", kind, 0, 10));
            dialog.CancelCommand.Execute(null);
            await cancelling;
            Assert.Single(await runtime.Messages.LoadPageAsync("tab", kind, 0, 10));

            var closing = command.ExecuteAsync(null);
            Assert.Single(owner.OwnedWindows.OfType<ConfirmationWindow>()).Close();
            await closing;
            Assert.Single(await runtime.Messages.LoadPageAsync("tab", kind, 0, 10));

            view.Model.IsReadingHistory = true;
            var clearing = command.ExecuteAsync(null);
            Assert.Single(owner.OwnedWindows.OfType<ConfirmationWindow>()).ConfirmCommand.Execute(null);
            await clearing;
            Pump(owner);
            Assert.Empty(view.Model.Items);
            Assert.Empty(view.Model.PinnedItems);
            Assert.Empty(view.Model.ScrollingItems);
            Assert.Equal(0, view.Model.UnreadCount);
            Assert.False(view.Model.IsReadingHistory);
            foreach (var other in Enum.GetValues<MessageKind>().Where(other => other != kind))
            {
                Assert.Single(await runtime.Messages.LoadPageAsync("tab", other, 0, 10));
            }
            Assert.Single(await runtime.Messages.LoadPageAsync("second", kind, 0, 10));
            await view.Model.LoadMoreAsync();
            Assert.Empty(view.Model.Items);
            await view.Model.SelectRoomAsync(null);
            Assert.False(button.IsEffectivelyEnabled);
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            await shell.CloseAsync();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>确认前切换房间仍只删除弹窗中指定的房间，当前列表继续展示新房间。</summary>
    [AvaloniaFact]
    public async Task ConfirmationKeepsOriginalTargetWhenRoomChanges()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        await runtime.Messages.AppendAsync(Message(MessageKind.Danmu), new());
        var second = Message(MessageKind.Danmu) with { RoomTabId = "second" };
        await runtime.Messages.AppendAsync(second, new());
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, MessageKind.Danmu, errors.Add);
        var actions = view.HeaderActionsTemplate.Build(view);
        var window = new Window { Content = new StackPanel { Children = { actions!, view } } };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "tab", Name = "原房间" });
            window.Show();
            Pump(window);
            var clearing = Assert.IsAssignableFrom<IAsyncRelayCommand>(FindClearButton(window).Command).ExecuteAsync(null);
            var dialog = Assert.Single(window.OwnedWindows.OfType<ConfirmationWindow>());
            await view.Model.SelectRoomAsync(new() { Id = "second", Name = "新房间" });
            dialog.ConfirmCommand.Execute(null);
            await clearing;
            Assert.Empty(await runtime.Messages.LoadPageAsync("tab", MessageKind.Danmu, 0, 10));
            Assert.Equal(second.Id, Assert.Single(view.Model.Items).Record.Id);
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    private static MessageRecord Message(MessageKind kind)
    {
        return new() { RoomTabId = "tab", Kind = kind, Content = "正文", TotalPrice = 30 };
    }

    private static Button FindClearButton(Window window)
    {
        return Assert.Single(window.GetVisualDescendants().OfType<Button>(), button => button.Name == "ClearFeedButton");
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
