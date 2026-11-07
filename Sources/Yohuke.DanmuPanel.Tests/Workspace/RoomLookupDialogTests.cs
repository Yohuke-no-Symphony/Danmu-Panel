using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Models.Workspace;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Views;
using Yohuke.DanmuPanel.Views.Components;
using Yohuke.DanmuPanel.Views.Settings;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Workspace;

/// <summary>验证房间与用户查找共享双页表面，并保留房间表单、结果动作和关闭生命周期。</summary>
public sealed class RoomLookupDialogTests
{
    /// <summary>新房间和特别关注查找均承载同一双页组件，房间默认显示自动搜索。</summary>
    [AvaloniaFact]
    public async Task RoomAndUserLookupShareTheSameTwoTabSurface()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        var search = new ControlledWorkspaceSearchService();
        var window = new RoomEditorWindow(runtime, new(), new(), false, search);
        try
        {
            window.Show();
            Pump(window);
            var lookup = Find<SearchAndManualDialogSurface>(window, "LookupDialog");
            var userSurface = new SpecialAttentionSearchSurface();

            Assert.IsType<SearchAndManualDialogSurface>(userSurface.FindControl<SearchAndManualDialogSurface>("LookupDialog"));
            Assert.Equal(SearchAndManualDialogMode.Search, lookup.SelectedMode);
            Assert.True(lookup.IsSearchEnabled);
            Assert.Equal(2, Find<TabControl>(lookup, "SearchAndManualTabs").Items.Count);
            Assert.True(Find<TabItem>(lookup, "AutomaticSearchTab").IsEffectivelyEnabled);
            Assert.True(Find<TextBox>(window, "LiveRoomSearchTextBox").IsEffectivelyVisible);
            Assert.Empty(search.Requests);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    /// <summary>编辑现有房间直接进入手动页，保留只读房号并禁用自动搜索页。</summary>
    [AvaloniaFact]
    public async Task ExistingRoomStartsInManualWithReadOnlyIdAndDisabledSearch()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        var room = new RoomTabState { RoomId = "123", Name = "已有直播间", OwnerUid = "456" };
        var search = new ControlledWorkspaceSearchService();
        var window = new RoomEditorWindow(runtime, new(), room, true, search);
        try
        {
            window.Show();
            Pump(window);
            var lookup = Find<SearchAndManualDialogSurface>(window, "LookupDialog");
            var id = Find<TextBox>(window, "ManualRoomIdTextBox");

            Assert.Equal(SearchAndManualDialogMode.Manual, lookup.SelectedMode);
            Assert.False(lookup.IsSearchEnabled);
            Assert.False(Find<TabItem>(lookup, "AutomaticSearchTab").IsEffectivelyEnabled);
            Assert.True(id.IsReadOnly);
            Assert.True(id.IsEffectivelyVisible);
            Assert.Equal("123", id.Text);
            Assert.Equal("已有直播间", Find<TextBox>(window, "ManualRoomNameTextBox").Text);
            Assert.Equal("456", Find<TextBox>(window, "ManualRoomOwnerUidTextBox").Text);
            Assert.Empty(search.Requests);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    /// <summary>表单、账号和校验错误跨页保留，修正房号后通过实际确认按钮写回并关闭。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualDraftAccountAndValidationSurviveTabSwitchesUntilConfirmation(bool dark)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        var room = new RoomTabState();
        var search = new ControlledWorkspaceSearchService();
        var window = new RoomEditorWindow(runtime, new(), room, false, search)
        {
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light
        };
        try
        {
            window.Show();
            Pump(window);
            var lookup = Find<SearchAndManualDialogSurface>(window, "LookupDialog");
            lookup.SelectedMode = SearchAndManualDialogMode.Manual;
            Pump(window);
            var account = Assert.Single(window.GetVisualDescendants().OfType<ComboBox>());
            var chosenAccount = window.Model.AccountChoices.Single(choice => choice.Id == "second-account");
            account.SelectedItem = chosenAccount;
            Find<TextBox>(window, "ManualRoomNameTextBox").Text = "  新直播间  ";
            Find<TextBox>(window, "ManualRoomIdTextBox").Text = "invalid";
            Find<TextBox>(window, "ManualRoomOwnerUidTextBox").Text = "456";
            Pump(window);
            var confirm = Find<Button>(window, "ManualRoomConfirmButton");
            Assert.Same(window.Model.ConfirmCommand, confirm.Command);
            confirm.Command!.Execute(confirm.CommandParameter);
            Pump(window);

            Assert.True(window.IsVisible);
            Assert.Equal("", room.RoomId);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
                text => text.IsEffectivelyVisible && text.Text == Localization.Get("Room.InvalidRoomId"));

            lookup.SelectedMode = SearchAndManualDialogMode.Search;
            Pump(window);

            Assert.Same(chosenAccount, window.Model.SelectedAccount);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
                text => text.IsEffectivelyVisible && text.Text == Localization.Get("Room.InvalidRoomId"));
            Assert.Empty(search.Requests);

            lookup.SelectedMode = SearchAndManualDialogMode.Manual;
            Pump(window);

            Assert.Equal("  新直播间  ", Find<TextBox>(window, "ManualRoomNameTextBox").Text);
            Assert.Equal("invalid", Find<TextBox>(window, "ManualRoomIdTextBox").Text);
            Assert.Equal("456", Find<TextBox>(window, "ManualRoomOwnerUidTextBox").Text);
            Assert.Same(chosenAccount, Assert.Single(window.GetVisualDescendants().OfType<ComboBox>()).SelectedItem);
            Capture(window, dark ? "room-lookup-manual-dark.png" : "room-lookup-manual-light.png");
            Find<TextBox>(window, "ManualRoomIdTextBox").Text = "123";
            Pump(window);
            confirm.Command.Execute(confirm.CommandParameter);

            Assert.False(window.IsVisible);
            Assert.Equal("123", room.RoomId);
            Assert.Equal("新直播间", room.Name);
            Assert.Equal("456", room.OwnerUid);
            Assert.Equal("second-account", room.AccountOverrideId);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    /// <summary>共享表面的结果按钮仍绑定当前房间模型及当前结果，避免插槽命名作用域误绑。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SearchResultActionUsesItsCurrentModelAndResult(bool dark)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        var room = new RoomTabState();
        var search = new ControlledWorkspaceSearchService();
        var window = new RoomEditorWindow(runtime, new(), room, false, search)
        {
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light
        };
        Task searching = Task.CompletedTask;
        try
        {
            window.Show();
            Pump(window);
            Find<TextBox>(window, "LiveRoomSearchTextBox").Text = "查找主播";
            Pump(window);
            var searchButton = Assert.Single(window.GetVisualDescendants().OfType<Button>(),
                button => ReferenceEquals(button.Command, window.Model.SearchLiveRoomsCommand));
            searching = Assert.IsAssignableFrom<IAsyncRelayCommand>(searchButton.Command).ExecuteAsync(null);
            Assert.Equal("查找主播", Assert.Single(search.Requests).Query);
            var target = new LiveRoomSearchResult { RoomId = "200", OwnerUid = "900", OwnerName = "目标主播" };
            search.Responses[0].SetResult(new()
            {
                Results = [new() { RoomId = "100", OwnerUid = "800", OwnerName = "其他主播" }, target]
            });
            await searching.WaitAsync(TimeSpan.FromSeconds(5));
            Pump(window);
            var add = Assert.Single(window.GetVisualDescendants().OfType<Button>(),
                button => button.Name == "AddSearchedRoomButton" && ReferenceEquals(button.CommandParameter, target));

            Assert.Same(window.Model.AddSearchedRoomCommand, add.Command);
            Assert.True(add.Command!.CanExecute(add.CommandParameter));
            Capture(window, dark ? "room-lookup-search-dark.png" : "room-lookup-search-light.png");
            add.Command.Execute(add.CommandParameter);

            Assert.False(window.IsVisible);
            Assert.Equal("200", room.RoomId);
            Assert.Equal("900", room.OwnerUid);
            Assert.Equal("目标主播", room.Name);
        }
        finally
        {
            CloseWindow(window);
            foreach (var response in search.Responses)
            {
                response.TrySetResult(new());
            }
            await searching.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>关闭真实房间窗口会取消搜索，迟到响应不能回填结果或修改房间。</summary>
    [AvaloniaFact]
    public async Task ClosingRoomWindowCancelsPendingSearchAndRejectsLateResult()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        var room = new RoomTabState();
        var search = new ControlledWorkspaceSearchService();
        var window = new RoomEditorWindow(runtime, new(), room, false, search);
        Task searching = Task.CompletedTask;
        try
        {
            window.Show();
            Pump(window);
            Find<TextBox>(window, "LiveRoomSearchTextBox").Text = "viewer";
            Pump(window);
            searching = window.Model.SearchLiveRoomsCommand.ExecuteAsync(null);
            var request = Assert.Single(search.Requests);

            window.Close();

            Assert.True(request.Cancellation.IsCancellationRequested);
            search.Responses[0].SetResult(new() { Results = [new() { RoomId = "123", OwnerName = "迟到主播" }] });
            await searching.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Empty(window.Model.LiveRoomSearchResults);
            Assert.False(window.Model.SearchLiveRoomsCommand.CanExecute(null));
            Assert.Equal("", room.RoomId);
            Assert.Equal("", room.Name);
        }
        finally
        {
            CloseWindow(window);
            foreach (var response in search.Responses)
            {
                response.TrySetResult(new());
            }
            await searching.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static PanelRuntime CreateRuntime(MessageStoreTestDirectory directory)
    {
        return new(new(directory.Path), new()
        {
            Accounts =
            [
                new() { Id = "first-account", Name = "第一个账号" },
                new() { Id = "second-account", Name = "第二个账号" }
            ]
        });
    }

    private static T Find<T>(Control owner, string name) where T : Control
    {
        return Assert.Single(owner.GetVisualDescendants().OfType<T>(), control => control.Name == name);
    }

    private static void CloseWindow(Window window)
    {
        foreach (var owned in window.OwnedWindows.ToArray())
        {
            CloseWindow(owned);
        }
        window.Close();
    }

    private static void Pump(Window window)
    {
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Capture(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("DANMUPANEL_SHARED_LOOKUP_SCREENSHOTS") is not { Length: > 0 } directory)
        {
            return;
        }
        Directory.CreateDirectory(directory);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(directory, name));
    }
}
