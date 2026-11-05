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
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Tests.Translation;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>通过真实消息行验证点击、切房间和列表释放行为。</summary>
public sealed class MessageFeedInteractionTests
{
    /// <summary>菜单重译已完成消息时重新请求模型，更新译文与缓存，并保留其他消息的已有译文。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.Danmu)]
    [InlineData(MessageKind.SuperChat)]
    public async Task RetryMenuRefreshesCompletedTranslationInsteadOfReusingCache(MessageKind kind)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        var settings = runtime.Configuration.Settings.Translation;
        settings.Enabled = true;
        settings.Model = "test";
        var fingerprint = TranslationRequestFactory.GetConfigurationFingerprint(settings);
        var message = Message(kind);
        var other = Message(kind);
        foreach (var record in new[] { message, other })
        {
            await runtime.Messages.AppendAsync(record, new(), fingerprint);
            Assert.True(await runtime.Messages.ApplyTranslationAsync(record.Id, record.Generation, fingerprint, "旧译文"));
        }
        await using var cache = new SqliteTranslationCache(":memory:");
        var request = TranslationRequestFactory.Create(settings, message.Content)!;
        await cache.SetAsync(request.CacheKey, "旧译文");
        var calls = 0;
        await using var service = new TranslationService(new DelegateTranslationClient((_, _) =>
        {
            calls++;
            return Task.FromResult("新译文");
        }), cache);
        await using var scheduler = new TranslationScheduler(runtime.Messages, service);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.Completed += (_, _) => completed.TrySetResult();
        Exception? error = null;
        var view = new MessageFeedView(runtime, kind, value => error = value);
        var window = new Window { Width = 800, Height = 600, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "tab", RoomId = "123" });
            await view.Model.WaitForPresentationAsync();
            window.Show();
            window.UpdateLayout();
            var row = FindRow(view, message.Id);
            var menu = Assert.IsType<LocalizedContextMenu>(row.ContextMenu);
            var retry = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, Localization.Get("Panel.RetryTranslation")));

            retry.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await WaitUntilAsync(() => Task.FromResult(retry.IsEnabled));

            Assert.Null(error);
            Assert.Equal(message.Id, Assert.Single(await runtime.Messages.GetPendingAsync(10)).MessageId);
            Assert.Null((await runtime.Messages.GetByIdAsync(message.Id))!.Translation);
            await scheduler.StartAsync(settings);
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await view.Model.RefreshAsync();
            await view.Model.WaitForPresentationAsync();

            Assert.Equal(1, calls);
            Assert.Equal("新译文", (await runtime.Messages.GetByIdAsync(message.Id))!.Translation);
            Assert.Equal("新译文", view.Model.Items.Single(item => item.Record.Id == message.Id).Translation);
            Assert.Equal("新译文", await cache.GetAsync(request.CacheKey));
            Assert.Equal("旧译文", (await runtime.Messages.GetByIdAsync(other.Id))!.Translation);
            Assert.Equal("新译文", await service.TranslateAsync(request));
            Assert.Equal(1, calls);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

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
            Assert.Equal(older.Id, view.Model.Items[0].Record.Id);
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
            Assert.Contains(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, Localization.Get("Panel.BackToLatest")));
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
            var firstActions = Assert.IsType<StackPanel>(view.HeaderActionsTemplate.Build(view));
            var secondActions = Assert.IsType<StackPanel>(view.HeaderActionsTemplate.Build(view));
            var first = Assert.IsType<Button>(firstActions.Children[^1]);
            var second = Assert.IsType<Button>(secondActions.Children[^1]);
            var clear = Assert.IsType<Button>(firstActions.Children[0]);
            Assert.NotSame(clear, secondActions.Children[0]);
            Assert.Equal(LucideIconKind.Trash2, Assert.IsType<LucideIcon>(clear.Content).Kind);
            Assert.NotSame(first, second);
            Assert.Contains("iconButton", first.Classes);
            Assert.Equal(LucideIconKind.ArrowDownToLine, Assert.IsType<LucideIcon>(first.Content).Kind);
            foreach (var language in new[] { "en-US", "ja-JP", "zh-CN" })
            {
                Localization.SetLanguage(language);
                Assert.Equal(Localization.Get("Panel.BackToLatest"), ToolTip.GetTip(first));
                Assert.Equal(Localization.Get("Panel.BackToLatest"), AutomationProperties.GetName(first));
                Assert.IsType<LucideIcon>(first.Content);
                Assert.Equal(Localization.Get("Panel.ClearFeed"), ToolTip.GetTip(clear));
                Assert.Equal(Localization.Get("Panel.ClearFeed"), AutomationProperties.GetName(clear));
                Assert.IsType<LucideIcon>(clear.Content);
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
            Assert.Same(first, view.Model.Items[5]);
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
