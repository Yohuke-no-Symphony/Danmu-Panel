using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views;
using Yohuke.DanmuPanel.Views.Components;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证特别关注右键操作的持久化、跨房间原位更新和失败恢复。</summary>
public sealed class SpecialAttentionInteractionTests
{
    /// <summary>右键关注跨房间同步已加载弹幕，保留消息对象且不重读数据库或改变置顶。</summary>
    [AvaloniaFact]
    public async Task ContextMenuUpdatesBothRoomsWithoutReloadingMessages()
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = new PanelConfiguration { Settings = new() { ShowUserAvatars = false } };
        await using var runtime = new PanelRuntime(new(directory.Path), configuration);
        var first = new MessageRecord { RoomTabId = "first", UserUid = 42, UserName = "关注用户", Content = "第一房间" };
        var second = first with { Id = Guid.NewGuid().ToString("N"), RoomTabId = "second", Content = "第二房间" };
        var other = first with { Id = Guid.NewGuid().ToString("N"), UserUid = 73, Content = "普通用户" };
        foreach (var message in new[] { first, second, other })
        {
            await runtime.Messages.AppendAsync(message, new());
        }
        var errors = new List<Exception>();
        var firstView = new MessageFeedView(runtime, MessageKind.Danmu, errors.Add);
        var secondView = new MessageFeedView(runtime, MessageKind.Danmu, errors.Add);
        var window = new Window { Width = 1000, Height = 700, Content = new StackPanel { Children = { firstView, secondView } } };
        try
        {
            await firstView.Model.SelectRoomAsync(new() { Id = "first" });
            await secondView.Model.SelectRoomAsync(new() { Id = "second" });
            await firstView.Model.WaitForPresentationAsync();
            await secondView.Model.WaitForPresentationAsync();
            window.Show();
            window.UpdateLayout();
            var firstItem = firstView.Model.Items.Single(item => item.Record.Id == first.Id);
            var secondItem = Assert.Single(secondView.Model.Items);
            var firstRecord = firstItem.Record;
            var row = firstView.GetVisualDescendants().OfType<MessageFeedRow>().Single(value => ReferenceEquals(value.DataContext, firstItem));
            var menu = row.ContextMenu!;
            menu.Open(row);
            var action = menu.Items.OfType<MenuItem>().Single(item => item.Name == "ToggleSpecialAttentionMenuItem");
            var reads = runtime.Messages.FeedVisibilityEvaluations;

            action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await WaitUntilAsync(() => action.IsEnabled && configuration.Settings.SpecialAttention.Users.Count == 1);

            Assert.True(firstItem.IsSpecialAttention);
            Assert.True(secondItem.IsSpecialAttention);
            Assert.False(firstView.Model.Items.Single(item => item.Record.Id == other.Id).IsSpecialAttention);
            Assert.Same(firstItem, firstView.Model.Items.Single(item => item.Record.Id == first.Id));
            Assert.Same(firstRecord, firstItem.Record);
            Assert.Equal(reads, runtime.Messages.FeedVisibilityEvaluations);
            Assert.False(firstItem.Record.IsPinned);
            Assert.Equal(42, Assert.Single(runtime.ConfigurationStore.Load().Settings.SpecialAttention.Users).UserUid);
            menu.Close();
            menu.Open(row);
            Assert.Equal(Localization.Get("SpecialAttention.Remove"), action.Header);

            action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await WaitUntilAsync(() => action.IsEnabled && configuration.Settings.SpecialAttention.Users.Count == 0);

            Assert.False(firstItem.IsSpecialAttention);
            Assert.False(secondItem.IsSpecialAttention);
            Assert.Equal(reads, runtime.Messages.FeedVisibilityEvaluations);
            Assert.Empty(errors);
        }
        finally
        {
            firstView.Model.Dispose();
            secondView.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>无有效身份的弹幕不能加入名单，菜单仍保留原有置顶首项。</summary>
    [AvaloniaFact]
    public async Task UnknownUidCannotBeAdded()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new() { Settings = new() { ShowUserAvatars = false } });
        await runtime.Messages.AppendAsync(new() { RoomTabId = "room", Content = "unknown" }, new());
        var view = new MessageFeedView(runtime, MessageKind.Danmu, _ => { });
        var window = new Window { Width = 800, Height = 300, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "room" });
            window.Show();
            window.UpdateLayout();
            var row = Assert.Single(view.GetVisualDescendants().OfType<MessageFeedRow>());
            row.ContextMenu!.Open(row);
            var action = row.ContextMenu.Items.OfType<MenuItem>().Single(item => item.Name == "ToggleSpecialAttentionMenuItem");

            Assert.False(action.IsEnabled);
            Assert.Equal(Localization.Get("Panel.PinMessage"), Assert.IsType<MenuItem>(row.ContextMenu.Items[0]).Header);
            Assert.Empty(runtime.Configuration.Settings.SpecialAttention.Users);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>写盘失败时恢复原名单且不发布成功通知，重复操作保持幂等。</summary>
    [AvaloniaFact]
    public async Task FailedSaveRollsBackAndSuccessfulChangesAreIdempotent()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var original = runtime.Configuration.Settings.SpecialAttention;
        var notifications = 0;
        runtime.SpecialAttentionChanged += () => notifications++;
        var temporary = Path.Combine(directory.Path, "settings.json.tmp");
        Directory.CreateDirectory(temporary);
        try
        {
            var error = await Record.ExceptionAsync(() => runtime.SetSpecialAttentionAsync(42, "用户", true));
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.Same(original, runtime.Configuration.Settings.SpecialAttention);
            Assert.Equal(0, notifications);
        }
        finally
        {
            Directory.Delete(temporary);
        }

        await runtime.SetSpecialAttentionAsync(42, "用户", true);
        await runtime.SetSpecialAttentionAsync(42, "用户", true);
        Assert.Equal(1, notifications);
        Assert.Single(runtime.Configuration.Settings.SpecialAttention.Users);
        await runtime.SetSpecialAttentionAsync(42, "用户", false);
        await runtime.SetSpecialAttentionAsync(42, "用户", false);
        Assert.Equal(2, notifications);
        Assert.Empty(runtime.ConfigurationStore.Load().Settings.SpecialAttention.Users);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Assert.True(condition());
    }
}
