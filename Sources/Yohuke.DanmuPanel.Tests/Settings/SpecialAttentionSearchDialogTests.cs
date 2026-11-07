using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Tests.Workspace;
using Yohuke.DanmuPanel.Views.Components;
using Yohuke.DanmuPanel.Views.Settings;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Settings;

/// <summary>验证用户查找通过所属设置窗口的模态弹窗完成，并共享可取消的名单草稿。</summary>
public sealed class SpecialAttentionSearchDialogTests
{
    /// <summary>特别关注页只保留查找入口，点击后创建所属窗口并等待弹窗关闭。</summary>
    [AvaloniaFact]
    public async Task SearchEntryOpensOwnedDialogWithoutInlineSearchForm()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var search = new ControlledUserSearchService();
        var view = CreateView(runtime, search);
        var window = new Window { Width = 1020, Height = 900, Content = view };
        try
        {
            ShowSpecialAttention(window, view);
            var editor = view.SpecialAttentionEditor;
            Assert.DoesNotContain(editor.GetVisualDescendants().OfType<TextBox>(),
                input => input.Name is "SpecialAttentionSearchInput" or "SpecialAttentionManualUid");

            var dialog = OpenSearchDialog(window, view);
            var pending = editor.PendingSearchDialog;

            Assert.Same(window, dialog.Owner);
            Assert.True(dialog.IsVisible);
            Assert.False(pending.IsCompleted);
            Assert.Same(editor.Model, dialog.Model);
            var lookup = Find<SearchAndManualDialogSurface>(dialog, "LookupDialog");
            Assert.Equal(SearchAndManualDialogMode.Search, lookup.SelectedMode);
            Assert.Equal(2, Find<TabControl>(lookup, "SearchAndManualTabs").Items.Count);
            Assert.True(Find<TextBox>(dialog, "SpecialAttentionSearchInput").IsEffectivelyVisible);
            Assert.DoesNotContain(editor.GetVisualDescendants().OfType<TextBox>(),
                input => input.Name == "SpecialAttentionSearchInput");

            Find<Button>(dialog, "CloseSpecialAttentionSearchButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Pump(window);

            Assert.Empty(window.OwnedWindows);
            Assert.True(pending.IsCompletedSuccessfully);
            Assert.Empty(search.Requests);
        }
        finally
        {
            CloseOwnedDialogs(window);
            window.Close();
            await view.SpecialAttentionEditor.PendingSearchDialog.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>关闭查找或卸载编辑器均取消当前请求，迟到结果不能改变共享草稿。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosingDialogOrDetachingEditorCancelsSearchAndRejectsLateResults(bool detachEditor)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var search = new ControlledUserSearchService();
        var view = CreateView(runtime, search);
        var window = new Window { Width = 1020, Height = 900, Content = view };
        Task searching = Task.CompletedTask;
        try
        {
            ShowSpecialAttention(window, view);
            var dialog = OpenSearchDialog(window, view);
            Find<TextBox>(dialog, "SpecialAttentionSearchInput").Text = "viewer";
            Pump(dialog);
            var command = Assert.IsAssignableFrom<IAsyncRelayCommand>(Find<Button>(dialog, "SpecialAttentionSearchButton").Command);
            searching = command.ExecuteAsync(null);
            Assert.Equal("viewer", Assert.Single(search.Requests).Query);
            Assert.True(view.SpecialAttentionEditor.Model.IsSearching);
            var pending = view.SpecialAttentionEditor.PendingSearchDialog;

            if (detachEditor)
            {
                window.Content = new TextBlock();
            }
            else
            {
                dialog.Close();
            }
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Pump(window);

            Assert.True(search.Requests[0].Cancellation.IsCancellationRequested);
            Assert.False(view.SpecialAttentionEditor.Model.IsSearching);
            Assert.False(dialog.IsVisible);
            Assert.Empty(window.OwnedWindows);
            search.Responses[0].SetResult(new() { Results = [new() { UserUid = 42, DisplayName = "迟到用户" }] });
            await searching.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Empty(view.SpecialAttentionEditor.Model.SearchResults);
            Assert.Empty(view.SpecialAttentionEditor.Model.Users);
            Assert.Empty(runtime.Configuration.Settings.SpecialAttention.Users);
        }
        finally
        {
            CloseOwnedDialogs(window);
            foreach (var response in search.Responses)
            {
                response.TrySetResult(new());
            }
            await searching.WaitAsync(TimeSpan.FromSeconds(5));
            window.Close();
            await view.SpecialAttentionEditor.PendingSearchDialog.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>手动添加后关闭不写盘，重新打开可继续搜索添加且保留同一名单草稿。</summary>
    [AvaloniaFact]
    public async Task ReopeningDialogKeepsManualDraftAndAllowsAnotherSearchAddition()
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = new PanelConfiguration
        {
            Settings = new() { SpecialAttention = new() { Users = [new() { UserUid = 42, DisplayName = "已有用户" }] } }
        };
        await using var runtime = new PanelRuntime(new(directory.Path), configuration);
        var search = new ControlledUserSearchService();
        var view = CreateView(runtime, search);
        var window = new Window { Width = 1020, Height = 900, Content = view };
        Task searching = Task.CompletedTask;
        try
        {
            ShowSpecialAttention(window, view);
            var editor = view.SpecialAttentionEditor;
            var first = OpenSearchDialog(window, view);
            Find<SearchAndManualDialogSurface>(first, "LookupDialog").SelectedMode = SearchAndManualDialogMode.Manual;
            Pump(first);
            Find<TextBox>(first, "SpecialAttentionManualUid").Text = "73";
            Find<TextBox>(first, "SpecialAttentionManualName").Text = "手动添加用户";
            Pump(first);
            var manualAdd = Find<Button>(first, "AddSpecialAttentionManualUserButton");
            manualAdd.Command!.Execute(manualAdd.CommandParameter);
            Pump(first);
            Assert.Equal(new long[] { 42, 73 }, editor.Model.Users.Select(user => user.UserUid));
            var firstPending = editor.PendingSearchDialog;

            first.Close();
            await firstPending.WaitAsync(TimeSpan.FromSeconds(5));
            Pump(window);

            Assert.Equal(42, Assert.Single(configuration.Settings.SpecialAttention.Users).UserUid);
            Assert.False(File.Exists(Path.Combine(directory.Path, "settings.json")));
            Assert.Contains(editor.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "手动添加用户");

            var second = OpenSearchDialog(window, view);
            Assert.NotSame(first, second);
            Assert.Same(editor.Model, second.Model);
            Assert.Equal(new long[] { 42, 73 }, second.Model.Users.Select(user => user.UserUid));
            Find<SearchAndManualDialogSurface>(second, "LookupDialog").SelectedMode = SearchAndManualDialogMode.Search;
            Pump(second);
            Find<TextBox>(second, "SpecialAttentionSearchInput").Text = "另一个用户";
            Pump(second);
            searching = Assert.IsAssignableFrom<IAsyncRelayCommand>(Find<Button>(second, "SpecialAttentionSearchButton").Command)
                .ExecuteAsync(null);
            Assert.Equal("另一个用户", Assert.Single(search.Requests).Query);
            search.Responses[0].SetResult(new() { Results = [new() { UserUid = 99, DisplayName = "搜索添加用户" }] });
            await searching.WaitAsync(TimeSpan.FromSeconds(5));
            Pump(second);
            var searchAdd = Find<Button>(second, "AddSpecialAttentionSearchResultButton");
            searchAdd.Command!.Execute(searchAdd.CommandParameter);
            Pump(second);
            var secondPending = editor.PendingSearchDialog;
            second.Close();
            await secondPending.WaitAsync(TimeSpan.FromSeconds(5));
            Pump(window);

            Assert.Equal(new long[] { 42, 73, 99 }, editor.Model.Users.Select(user => user.UserUid));
            Assert.Equal(42, Assert.Single(configuration.Settings.SpecialAttention.Users).UserUid);
            Assert.False(File.Exists(Path.Combine(directory.Path, "settings.json")));
            Assert.Empty(window.OwnedWindows);
        }
        finally
        {
            CloseOwnedDialogs(window);
            foreach (var response in search.Responses)
            {
                response.TrySetResult(new());
            }
            await searching.WaitAsync(TimeSpan.FromSeconds(5));
            window.Close();
            await view.SpecialAttentionEditor.PendingSearchDialog.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>切换手动页保留进行中的查找，返回搜索页显示结果并保留未提交的手动输入。</summary>
    [AvaloniaFact]
    public async Task SwitchingTabsPreservesManualDraftAndPendingSearch()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var search = new ControlledUserSearchService();
        var view = CreateView(runtime, search);
        var window = new Window { Width = 1020, Height = 900, Content = view };
        Task searching = Task.CompletedTask;
        try
        {
            ShowSpecialAttention(window, view);
            var dialog = OpenSearchDialog(window, view);
            var lookup = Find<SearchAndManualDialogSurface>(dialog, "LookupDialog");
            Find<TextBox>(dialog, "SpecialAttentionSearchInput").Text = "viewer";
            Pump(dialog);
            searching = Assert.IsAssignableFrom<IAsyncRelayCommand>(Find<Button>(dialog, "SpecialAttentionSearchButton").Command)
                .ExecuteAsync(null);
            var request = Assert.Single(search.Requests);

            lookup.SelectedMode = SearchAndManualDialogMode.Manual;
            Pump(dialog);
            Find<TextBox>(dialog, "SpecialAttentionManualUid").Text = "73";
            Find<TextBox>(dialog, "SpecialAttentionManualName").Text = "保留的手动草稿";
            Pump(dialog);

            Assert.True(dialog.Model.IsSearching);
            Assert.False(request.Cancellation.IsCancellationRequested);
            Assert.Equal("73", dialog.Model.ManualUid);
            Assert.Equal("保留的手动草稿", dialog.Model.ManualDisplayName);
            search.Responses[0].SetResult(new() { Results = [new() { UserUid = 99, DisplayName = "查找完成的用户" }] });
            await searching.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(SearchAndManualDialogMode.Manual, lookup.SelectedMode);

            lookup.SelectedMode = SearchAndManualDialogMode.Search;
            Pump(dialog);

            Assert.Equal("viewer", Find<TextBox>(dialog, "SpecialAttentionSearchInput").Text);
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(),
                text => text.IsEffectivelyVisible && text.Text == "查找完成的用户");
            Assert.Single(search.Requests);
            Assert.False(request.Cancellation.IsCancellationRequested);

            lookup.SelectedMode = SearchAndManualDialogMode.Manual;
            Pump(dialog);

            Assert.Equal("73", Find<TextBox>(dialog, "SpecialAttentionManualUid").Text);
            Assert.Equal("保留的手动草稿", Find<TextBox>(dialog, "SpecialAttentionManualName").Text);
            Assert.Empty(dialog.Model.Users);
            Assert.Empty(runtime.Configuration.Settings.SpecialAttention.Users);
            Assert.False(File.Exists(Path.Combine(directory.Path, "settings.json")));
        }
        finally
        {
            CloseOwnedDialogs(window);
            foreach (var response in search.Responses)
            {
                response.TrySetResult(new());
            }
            await searching.WaitAsync(TimeSpan.FromSeconds(5));
            window.Close();
            await view.SpecialAttentionEditor.PendingSearchDialog.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static SettingsView CreateView(PanelRuntime runtime, ControlledUserSearchService search)
    {
        return new(runtime, new TextBlock(), false, userSearch: search, userAvatars: new ControlledUserAvatarService());
    }

    private static void ShowSpecialAttention(Window window, SettingsView view)
    {
        window.Show();
        Find<Button>(view, "SettingsSectionSpecialAttention").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(window);
    }

    private static SpecialAttentionSearchWindow OpenSearchDialog(Window owner, SettingsView view)
    {
        Find<Button>(view.SpecialAttentionEditor, "OpenSpecialAttentionSearchButton")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(owner);
        var dialog = Assert.IsType<SpecialAttentionSearchWindow>(Assert.Single(owner.OwnedWindows));
        Pump(dialog);
        return dialog;
    }

    private static T Find<T>(Control owner, string name) where T : Control
    {
        return Assert.Single(owner.GetVisualDescendants().OfType<T>(), control => control.Name == name);
    }

    private static void CloseOwnedDialogs(Window owner)
    {
        foreach (var dialog in owner.OwnedWindows.ToArray())
        {
            CloseOwnedDialogs(dialog);
            dialog.Close();
        }
    }

    private static void Pump(Window window)
    {
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        Dispatcher.UIThread.RunJobs();
    }
}
