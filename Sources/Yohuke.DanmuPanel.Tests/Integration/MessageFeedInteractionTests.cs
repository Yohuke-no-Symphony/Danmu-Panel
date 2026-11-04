using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Lucide.Avalonia;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views;
using Yohuke.DanmuPanel.Tests.Messages;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>通过真实消息行验证点击、切房间和列表释放行为。</summary>
public sealed class MessageFeedInteractionTests
{
    /// <summary>点击普通弹幕提交置顶，第二次点击取消并恢复时间排序。</summary>
    [AvaloniaFact]
    public async Task DanmuRowClickTogglesPersistedPin()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        var older = Message(MessageKind.Danmu) with { Time = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var newer = Message(MessageKind.Danmu);
        await runtime.Messages.AppendAsync(older, new());
        await runtime.Messages.AppendAsync(newer, new());
        Exception? error = null;
        var view = new MessageFeedView(runtime, MessageKind.Danmu, value => error = value);
        var window = new Window { Width = 800, Height = 600, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "tab", RoomId = "123" });
            window.Show();
            window.UpdateLayout();
            FindRow(view, older.Id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(async () => (await runtime.Messages.GetByIdAsync(older.Id))!.IsPinned);
            await view.Model.RefreshAsync();
            Assert.Equal(older.Id, view.Model.Items[0].Record.Id);

            window.UpdateLayout();
            FindRow(view, older.Id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(async () => !(await runtime.Messages.GetByIdAsync(older.Id))!.IsPinned);
            await view.Model.RefreshAsync();
            Assert.Equal(newer.Id, view.Model.Items[0].Record.Id);
            Assert.Null(error);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>点击 SC 提交已读，较早的未读条目仍留在顶部。</summary>
    [AvaloniaFact]
    public async Task SuperChatRowClickMarksReadAndPreservesUnreadPriority()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        var older = Message(MessageKind.SuperChat) with { Time = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var newer = Message(MessageKind.SuperChat);
        await runtime.Messages.AppendAsync(older, new());
        await runtime.Messages.AppendAsync(newer, new());
        Exception? error = null;
        var view = new MessageFeedView(runtime, MessageKind.SuperChat, value => error = value);
        var window = new Window { Width = 800, Height = 600, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "tab", RoomId = "123" });
            window.Show();
            window.UpdateLayout();
            FindRow(view, newer.Id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(async () => (await runtime.Messages.GetByIdAsync(newer.Id))!.IsRead);
            await view.Model.RefreshAsync();

            Assert.Equal(older.Id, view.Model.Items[0].Record.Id);
            Assert.False(view.Model.Items[0].Record.IsRead);
            Assert.Null(error);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>房间切换不复用其他标签的消息，释放后排队刷新不再写入列表。</summary>
    [AvaloniaFact]
    public async Task RoomSwitchAndDisposeInvalidateOldRefreshes()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        var first = Message(MessageKind.Danmu);
        var second = Message(MessageKind.Danmu) with { RoomTabId = "second" };
        await runtime.Messages.AppendAsync(first, new());
        await runtime.Messages.AppendAsync(second, new());
        using var model = new FeedViewModel(runtime, MessageKind.Danmu);
        await model.SelectRoomAsync(new() { Id = "tab", RoomId = "123" });
        Assert.Equal(first.Id, Assert.Single(model.Items).Record.Id);
        await model.SelectRoomAsync(new() { Id = "second", RoomId = "123" });
        Assert.Equal(second.Id, Assert.Single(model.Items).Record.Id);

        await runtime.ApplySettingsAsync();
        model.Dispose();
        var snapshot = model.Items.ToArray();
        Dispatcher.UIThread.RunJobs();
        await model.LoadMoreAsync();
        await model.RefreshAsync();
        await model.SelectRoomAsync(new() { Id = "tab", RoomId = "123" });
        Assert.Equal(snapshot, model.Items.ToArray());
    }

    /// <summary>显示模式保留无译文原文，目标语言改变不会展示旧配置译文。</summary>
    [AvaloniaFact]
    public async Task TranslationPresentationUsesCurrentConfigurationIdentity()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        var settings = runtime.Configuration.Settings;
        var fingerprint = Services.Translation.TranslationRequestFactory.GetConfigurationFingerprint(settings.Translation);
        var record = Message(MessageKind.Danmu) with { Translation = "hello", TranslationFingerprint = fingerprint };
        var item = new MessageItemViewModel(record, settings, runtime.Pinyin);
        Assert.True(item.HasTranslation);
        Assert.True(item.ShowOriginal);
        settings.Translation.DisplayMode = TranslationDisplayMode.TranslationOnly;
        item.Update(record, settings);
        Assert.False(item.ShowOriginal);
        settings.Translation.TargetLanguage = "ja-JP";
        item.Update(record, settings);
        Assert.False(item.HasTranslation);
        Assert.True(item.ShowOriginal);
    }

    /// <summary>消息菜单按类型提交阅读动作，并在正文区仅保留消息行按钮。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.Danmu)]
    [InlineData(MessageKind.SuperChat)]
    [InlineData(MessageKind.Gift)]
    [InlineData(MessageKind.Guard)]
    public async Task ContextMenuActsOnItsMessageWithoutExposedActionBars(MessageKind kind)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        var first = Message(kind) with { TotalPrice = 100 };
        var second = Message(kind) with { Time = first.Time.AddSeconds(1), TotalPrice = 100 };
        await runtime.Messages.AppendAsync(first, new());
        await runtime.Messages.AppendAsync(second, new());
        Exception? error = null;
        var view = new MessageFeedView(runtime, kind, value => error = value);
        var window = new Window { Width = 800, Height = 600, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "tab", RoomId = "123" });
            window.Show();
            window.UpdateLayout();
            var row = FindRow(view, first.Id);
            var menu = Assert.IsType<LocalizedContextMenu>(row.ContextMenu);
            var action = Assert.IsType<MenuItem>(menu.Items[0]);
            Assert.IsType<LucideIcon>(action.Icon);
            Assert.Contains(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, Localization.Get("Panel.MoreHistory")));
            Assert.Contains(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, Localization.Get("Panel.BackToTop")));
            action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await WaitUntilAsync(async () =>
            {
                var record = (await runtime.Messages.GetByIdAsync(first.Id))!;
                return kind == MessageKind.Danmu ? record.IsPinned : record.IsRead;
            });
            var unchanged = (await runtime.Messages.GetByIdAsync(second.Id))!;
            Assert.False(kind == MessageKind.Danmu ? unchanged.IsPinned : unchanged.IsRead);
            Assert.All(view.GetVisualDescendants().OfType<Button>().Where(button => button.GetType() == typeof(Button)), button =>
                Assert.IsType<MessageItemViewModel>(button.DataContext));
            Assert.Null(error);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>标题动作使用独立图标实例，语言变化更新提示而不会覆盖图标内容。</summary>
    [AvaloniaFact]
    public async Task HeaderActionsCreateIndependentLocalizedIconButtons()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        var view = new MessageFeedView(runtime, MessageKind.Danmu, exception => throw exception);
        var originalLanguage = Localization.SelectedLanguageID;
        try
        {
            var first = Assert.IsType<Button>(view.HeaderActionsTemplate.Build(view));
            var second = Assert.IsType<Button>(view.HeaderActionsTemplate.Build(view));
            Assert.NotSame(first, second);
            Assert.Contains("iconButton", first.Classes);
            Assert.Equal(LucideIconKind.ArrowUpToLine, Assert.IsType<LucideIcon>(first.Content).Kind);
            foreach (var language in new[] { "en-US", "ja-JP", "zh-CN" })
            {
                Localization.SetLanguage(language);
                Assert.Equal(Localization.Get("Panel.BackToTop"), ToolTip.GetTip(first));
                Assert.Equal(Localization.Get("Panel.BackToTop"), AutomationProperties.GetName(first));
                Assert.IsType<LucideIcon>(first.Content);
            }
        }
        finally
        {
            view.Model.Dispose();
            Localization.SetLanguage(originalLanguage);
        }
    }

    /// <summary>历史加载通过面板菜单补齐下一页，并保留已有消息对象。</summary>
    [AvaloniaFact]
    public async Task PanelContextMenuLoadsHistoryWithoutReplacingExistingRows()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        for (var index = 0; index < 105; index++)
        {
            await runtime.Messages.AppendAsync(Message(MessageKind.Danmu) with
            {
                Content = "message " + index,
                Time = DateTimeOffset.UtcNow.AddSeconds(index)
            }, new());
        }
        var view = new MessageFeedView(runtime, MessageKind.Danmu, exception => throw exception);
        var window = new Window { Width = 800, Height = 600, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "tab", RoomId = "123" });
            window.Show();
            window.UpdateLayout();
            var first = view.Model.Items[0];
            Assert.Equal(100, view.Model.Items.Count);
            var list = Assert.Single(view.GetVisualDescendants().OfType<ListBox>(), item => item.Name == "ScrollingMessages");
            var more = Assert.IsType<MenuItem>(list.ContextMenu!.Items[0]);
            more.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await WaitUntilAsync(() => Task.FromResult(view.Model.Items.Count == 105));
            Assert.Same(first, view.Model.Items[0]);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    private static PanelRuntime CreateRuntime(MessageStoreTestDirectory directory)
    {
        return new(new ConfigurationService(directory.Path), new PanelConfiguration());
    }

    private static MessageRecord Message(MessageKind kind)
    {
        return new() { RoomTabId = "tab", RoomId = "123", Kind = kind, Content = "正文", UserName = "viewer", TotalPrice = 30 };
    }

    private static Button FindRow(MessageFeedView view, string id)
    {
        return view.GetVisualDescendants().OfType<Button>().Single(button =>
            button.DataContext is MessageItemViewModel item && item.Record.Id == id);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (await condition())
            {
                return;
            }
            await Task.Delay(10);
        }
        Assert.Fail("The message row action did not commit within the test timeout.");
    }
}
