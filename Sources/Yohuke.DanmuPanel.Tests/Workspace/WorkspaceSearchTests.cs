using Avalonia.Headless.XUnit;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.ViewModels.Workspace;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Workspace;

/// <summary>验证搜索代次、追加分页、关闭取消及本地化。</summary>
public sealed class WorkspaceSearchTests
{
    /// <summary>已完成搜索的问题随语言切换重绘，不重新请求平台。</summary>
    [AvaloniaFact]
    public async Task StructuredSearchIssueRendersAgainAfterLanguageChange()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var service = new ControlledWorkspaceSearchService();
        using var model = new RoomEditorViewModel(runtime, new(), new(), false, _ => { }, CancellationToken.None, service);
        var previous = Localization.SelectedLanguageID;
        try
        {
            Localization.SetLanguage("en-US");
            var searching = model.SearchAsync(false);
            service.Responses[0].SetResult(new()
            {
                Issues = [new()
                {
                    Source = LiveRoomSearchSource.Following, ApiCode = -101,
                    Error = new(CoreErrorCode.RoomSearchFollowingLoginRequired)
                }]
            });
            await searching;
            Assert.Equal(Localization.Get("Services.BilibiliRoomSearch.FollowingLoginRequired"), model.LiveRoomSearchStatus);

            Localization.SetLanguage("zh-CN");

            Assert.Equal(Localization.Get("Services.BilibiliRoomSearch.FollowingLoginRequired"), model.LiveRoomSearchStatus);
            Assert.Single(service.Requests);
        }
        finally
        {
            Localization.SetLanguage(previous);
        }
    }

    /// <summary>异常保留原类型与固定诊断，界面通过描述按当前语言显示。</summary>
    [AvaloniaFact]
    public async Task StructuredSearchExceptionPreservesTypeAndRendersAgain()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var service = new ControlledWorkspaceSearchService();
        using var model = new RoomEditorViewModel(runtime, new(), new(), false, _ => { }, CancellationToken.None, service);
        var previous = Localization.SelectedLanguageID;
        var error = CoreExceptionInfo.Create(CoreErrorCode.ApiEmptyJsonResult,
            message => new Newtonsoft.Json.JsonSerializationException(message));
        var diagnostic = error.Message;
        try
        {
            Localization.SetLanguage("en-US");
            var searching = model.SearchAsync(false);
            service.Responses[0].SetException(error);
            await searching;
            Assert.Equal(Localization.Get("Services.Api.EmptyJsonResult"), model.LiveRoomSearchStatus);

            Localization.SetLanguage("ja-JP");

            Assert.Equal(Localization.Get("Services.Api.EmptyJsonResult"), model.LiveRoomSearchStatus);
            Assert.IsType<Newtonsoft.Json.JsonSerializationException>(error);
            Assert.Equal(diagnostic, error.Message);
            Assert.Single(service.Requests);
        }
        finally
        {
            Localization.SetLanguage(previous);
        }
    }

    /// <summary>新查询隔离旧响应，后续分页追加并去重。</summary>
    [AvaloniaFact]
    public async Task LateResponseCannotReplaceNewQueryAndPaginationAppends()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var service = new ControlledWorkspaceSearchService();
        using var model = new RoomEditorViewModel(runtime, new(), new(), false, _ => { }, CancellationToken.None, service);
        model.LiveRoomSearchText = "old";
        var old = model.SearchAsync(false);
        Assert.False(model.SearchLiveRoomsCommand.CanExecute(null));
        Assert.False(model.LoadMoreLiveRoomsCommand.CanExecute(null));
        model.LiveRoomSearchText = "new";
        var current = model.SearchAsync(false);
        Assert.True(service.Requests[0].Cancellation.IsCancellationRequested);
        service.Responses[1].SetResult(new() { HasMore = true, Results = [new() { RoomId = "200", OwnerName = "New" }] });
        await current;
        service.Responses[0].SetResult(new() { Results = [new() { RoomId = "100", OwnerName = "Old" }] });
        await old;
        Assert.Equal("200", Assert.Single(model.LiveRoomSearchResults).RoomId);
        Assert.True(model.LoadMoreLiveRoomsCommand.CanExecute(null));
        var more = model.SearchAsync(true);
        Assert.Equal(("new", 2), (service.Requests[2].Query, service.Requests[2].Page));
        service.Responses[2].SetResult(new()
        {
            Results = [new() { RoomId = "200" }, new() { RoomId = "300", OwnerName = "Next" }]
        });
        await more;
        Assert.Equal(new[] { "200", "300" }, model.LiveRoomSearchResults.Select(result => result.RoomId));
        Assert.False(model.LoadMoreLiveRoomsCommand.CanExecute(null));
    }

    /// <summary>关闭搜索模型后忽略不遵守取消的迟到响应。</summary>
    [AvaloniaFact]
    public async Task ClosingCancelsSearchAndIgnoresLateResult()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var service = new ControlledWorkspaceSearchService();
        var model = new RoomEditorViewModel(runtime, new(), new(), false, _ => { }, CancellationToken.None, service);
        var request = model.SearchAsync(false);
        model.Dispose();
        Assert.True(service.Requests[0].Cancellation.IsCancellationRequested);
        service.Responses[0].SetResult(new() { Results = [new() { RoomId = "123" }] });
        await request;
        Assert.Empty(model.LiveRoomSearchResults);
        Assert.False(model.SearchLiveRoomsCommand.CanExecute(null));
    }

    /// <summary>切换语言同步更新弹窗标题和账号继承选项。</summary>
    [AvaloniaFact]
    public async Task LanguageSwitchUpdatesDialogAndInheritedAccountChoice()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var previous = Localization.SelectedLanguageID;
        using var model = new RoomEditorViewModel(runtime, new(), new(), false, _ => { }, CancellationToken.None);
        var properties = new List<string?>();
        model.PropertyChanged += (_, args) => properties.Add(args.PropertyName);
        try
        {
            Localization.SetLanguage("en-US");
            Localization.SetLanguage("ja-JP");
            Assert.Equal(Localization.Get("Panel.AddRoom"), model.AddDialogTitle);
            Assert.Equal(Localization.Get("Panel.InheritAccount"), model.SelectedAccount!.Name);
            Assert.Contains(nameof(model.AddDialogDescription), properties);
            Assert.Contains(nameof(model.AddDialogPrimaryText), properties);
        }
        finally
        {
            Localization.SetLanguage(previous);
        }
    }
}
