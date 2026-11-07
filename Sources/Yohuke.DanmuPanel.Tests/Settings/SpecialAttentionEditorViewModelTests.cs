using Avalonia.Headless.XUnit;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.ViewModels.Settings;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Settings;

/// <summary>验证特别关注的搜索竞态、分页及独立名单草稿。</summary>
public sealed class SpecialAttentionEditorViewModelTests
{
    /// <summary>手动添加拒绝无效或溢出的 UID，不污染名单。</summary>
    [AvaloniaTheory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.2")]
    [InlineData("abc")]
    [InlineData("+42")]
    [InlineData("１２３")]
    [InlineData("9223372036854775808")]
    public void ManualAddRejectsInvalidUid(string input)
    {
        using var model = new SpecialAttentionEditorViewModel(new(), new ControlledUserSearchService(), () => null);
        model.ManualUid = input;

        model.AddManualUserCommand.Execute(null);

        Assert.Empty(model.Users);
        Assert.NotEmpty(model.EditStatus);
    }

    /// <summary>草稿按 UID 去重、规范化前导零，并允许同名不同用户。</summary>
    [AvaloniaFact]
    public void DraftIsDetachedAndUsesUidIdentity()
    {
        var source = new SpecialAttentionSettings { Users = [new() { UserUid = 42, DisplayName = "观众" }] };
        using var model = new SpecialAttentionEditorViewModel(source, new ControlledUserSearchService(), () => null);
        model.ManualUid = "00042";
        model.AddManualUserCommand.Execute(null);
        Assert.Single(model.Users);
        Assert.NotEmpty(model.EditStatus);
        model.ManualUid = "73";
        model.ManualDisplayName = "  观众  ";
        model.AddManualUserCommand.Execute(null);

        Assert.Equal(new long[] { 42, 73 }, model.Users.Select(user => user.UserUid));
        Assert.All(model.Users, user => Assert.Equal("观众", user.DisplayName));
        Assert.Single(source.Users);
        var snapshot = model.CreateSnapshot();
        Assert.NotSame(model.Users[0], snapshot.Users[0]);
        model.RemoveUserCommand.Execute(model.Users[0]);
        Assert.Equal(2, snapshot.Users.Count);
        Assert.Single(source.Users);
    }

    /// <summary>新搜索取消旧请求，迟到的旧结果不能覆盖用户当前搜索。</summary>
    [AvaloniaFact]
    public async Task SearchIgnoresLateResultsAndPreservesSelectedUsers()
    {
        var search = new ControlledUserSearchService();
        using var model = new SpecialAttentionEditorViewModel(new(), search, () => "test-cookie");
        model.SearchText = "first";
        var first = model.SearchAsync(false);
        model.SearchText = "second";
        var second = model.SearchAsync(false);
        Assert.True(search.Requests[0].Cancellation.IsCancellationRequested);
        Assert.Equal("test-cookie", search.Requests[1].Cookie);
        search.Responses[1].SetResult(new() { Results = [new() { UserUid = 73, DisplayName = "当前用户" }] });
        await second;
        model.AddSearchedUserCommand.Execute(model.SearchResults[0]);
        search.Responses[0].SetResult(new() { Results = [new() { UserUid = 42, DisplayName = "旧用户" }] });
        await first;

        Assert.Equal(73, Assert.Single(model.SearchResults).UserUid);
        Assert.Equal(73, Assert.Single(model.Users).UserUid);
        Assert.False(model.AddSearchedUserCommand.CanExecute(model.SearchResults[0]));
        Assert.False(model.IsSearching);
    }

    /// <summary>分页追加按 UID 去重，失败页保留已显示结果并允许重试同一页。</summary>
    [AvaloniaFact]
    public async Task PaginationDeduplicatesAndFailedPageCanBeRetried()
    {
        var search = new ControlledUserSearchService();
        using var model = new SpecialAttentionEditorViewModel(new(), search, () => null);
        model.SearchText = "user";
        var first = model.SearchAsync(false);
        search.Responses[0].SetResult(new() { Results = [new() { UserUid = 42 }], HasMore = true });
        await first;
        var failed = model.SearchAsync(true);
        search.Responses[1].SetResult(new() { Issues = [new() { Message = "try again" }] });
        await failed;
        Assert.True(model.HasMoreResults);
        Assert.Single(model.SearchResults);
        var retry = model.SearchAsync(true);
        Assert.Equal(2, search.Requests[2].Page);
        search.Responses[2].SetResult(new() { Results = [new() { UserUid = 42 }, new() { UserUid = 73 }] });
        await retry;

        Assert.Equal(new long[] { 42, 73 }, model.SearchResults.Select(user => user.UserUid));
        Assert.False(model.HasMoreResults);
    }

    /// <summary>离开页面时取消请求，已关闭页面不能接受迟到结果。</summary>
    [AvaloniaFact]
    public async Task CancelSearchRejectsLateResults()
    {
        var search = new ControlledUserSearchService();
        using var model = new SpecialAttentionEditorViewModel(new(), search, () => null);
        model.SearchText = "viewer";
        var pending = model.SearchAsync(false);

        model.CancelSearch();
        search.Responses[0].SetResult(new() { Results = [new() { UserUid = 42 }] });
        await pending;

        Assert.True(search.Requests[0].Cancellation.IsCancellationRequested);
        Assert.Empty(model.SearchResults);
        Assert.False(model.IsSearching);
    }

    /// <summary>修改搜索文字立即取消旧查询并清空结果，防止添加已过期的搜索身份。</summary>
    [AvaloniaFact]
    public async Task EditingSearchTextCancelsRequestAndClearsOldResults()
    {
        var search = new ControlledUserSearchService();
        using var model = new SpecialAttentionEditorViewModel(new(), search, () => null);
        model.SearchText = "first";
        var first = model.SearchAsync(false);
        search.Responses[0].SetResult(new() { Results = [new() { UserUid = 42 }], HasMore = true });
        await first;
        var next = model.SearchAsync(true);

        model.SearchText = "changed";

        Assert.True(search.Requests[1].Cancellation.IsCancellationRequested);
        Assert.Empty(model.SearchResults);
        Assert.False(model.HasMoreResults);
        Assert.False(model.IsSearching);
        search.Responses[1].SetResult(new() { Results = [new() { UserUid = 73 }] });
        await next;
        Assert.Empty(model.SearchResults);
    }

    /// <summary>命令允许替换尚未结束的搜索，迟到任务不会改变当前忙碌状态。</summary>
    [AvaloniaFact]
    public async Task SearchCommandCanReplaceRunningQuery()
    {
        var search = new ControlledUserSearchService();
        using var model = new SpecialAttentionEditorViewModel(new(), search, () => null);
        model.SearchText = "viewer";
        var first = model.SearchCommand.ExecuteAsync(null);
        Assert.True(model.SearchCommand.CanExecute(null));

        var second = model.SearchCommand.ExecuteAsync(null);
        Assert.True(search.Requests[0].Cancellation.IsCancellationRequested);
        search.Responses[0].SetResult(new() { Results = [new() { UserUid = 42 }] });
        await first;
        Assert.True(model.IsSearching);
        search.Responses[1].SetResult(new() { Results = [new() { UserUid = 73 }] });
        await second;
        Assert.Equal(73, Assert.Single(model.SearchResults).UserUid);
        Assert.False(model.IsSearching);
    }

    /// <summary>名单添加和删除后通知命令可用性，已有用户不会被重复加入。</summary>
    [AvaloniaFact]
    public void UserChangesNotifySearchAddCommandAvailability()
    {
        using var model = new SpecialAttentionEditorViewModel(new(), new ControlledUserSearchService(), () => null);
        var notifications = 0;
        model.AddSearchedUserCommand.CanExecuteChanged += (_, _) => notifications++;
        var result = new UserSearchResult { UserUid = 42, DisplayName = "用户" };

        model.AddSearchedUserCommand.Execute(result);
        Assert.False(model.AddSearchedUserCommand.CanExecute(result));
        model.RemoveUserCommand.Execute(model.Users[0]);

        Assert.True(model.AddSearchedUserCommand.CanExecute(result));
        Assert.True(notifications >= 2);
    }

    /// <summary>保存草稿保留外部新增项，应用草稿删除项，且不复活已被外部删除的旧项。</summary>
    [AvaloniaFact]
    public void SnapshotMergesConcurrentMembershipChangesWithoutResurrectingUsers()
    {
        var initial = new SpecialAttentionSettings
        {
            Users = [new() { UserUid = 42, DisplayName = "原用户" }, new() { UserUid = 73, DisplayName = "将取消关注" }]
        };
        using var model = new SpecialAttentionEditorViewModel(initial, new ControlledUserSearchService(), () => null);
        model.RemoveUserCommand.Execute(model.Users.Single(user => user.UserUid == 73));
        model.ManualUid = "90";
        model.ManualDisplayName = "草稿新增";
        model.AddManualUserCommand.Execute(null);
        var current = new SpecialAttentionSettings
        {
            Users = [new() { UserUid = 73 }, new() { UserUid = 85, DisplayName = "右键新增" }]
        };

        var snapshot = model.CreateSnapshot(current);

        Assert.Equal(new long[] { 85, 90 }, snapshot.Users.Select(user => user.UserUid));
        Assert.Equal("右键新增", snapshot.Users[0].DisplayName);
        Assert.Equal("草稿新增", snapshot.Users[1].DisplayName);
        Assert.NotSame(current.Users[1], snapshot.Users[0]);
        Assert.NotSame(model.Users[1], snapshot.Users[1]);
        Assert.Equal(new long[] { 73, 85 }, current.Users.Select(user => user.UserUid));
        Assert.Equal(new long[] { 42, 73 }, initial.Users.Select(user => user.UserUid));
    }

    /// <summary>外部更新保留用户名称，并与草稿新增的同一 UID 合并为一个条目。</summary>
    [AvaloniaFact]
    public void SnapshotKeepsCurrentNamesAndDeduplicatesConcurrentAdditions()
    {
        var initial = new SpecialAttentionSettings { Users = [new() { UserUid = 42, DisplayName = "旧名称" }] };
        using var model = new SpecialAttentionEditorViewModel(initial, new ControlledUserSearchService(), () => null);
        model.ManualUid = "73";
        model.ManualDisplayName = "手动名称";
        model.AddManualUserCommand.Execute(null);
        var current = new SpecialAttentionSettings
        {
            Users = [new() { UserUid = 42, DisplayName = "新名称" }, new() { UserUid = 73, DisplayName = "外部名称" }]
        };

        var snapshot = model.CreateSnapshot(current);

        Assert.Equal(new long[] { 42, 73 }, snapshot.Users.Select(user => user.UserUid));
        Assert.Equal(new[] { "新名称", "外部名称" }, snapshot.Users.Select(user => user.DisplayName));
    }

    /// <summary>宿主显式刷新本地化时保留搜索问题及名单草稿，未显示模型不订阅全局事件。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitLocalizationRefreshPreservesStatusAndDraft(bool remoteIssue)
    {
        var previousLanguage = Localization.SelectedLanguageID;
        var search = new ControlledUserSearchService();
        using var model = new SpecialAttentionEditorViewModel(new(), search, () => null);
        try
        {
            Localization.SetLanguage("en-US");
            model.ManualUid = "42";
            model.ManualDisplayName = "草稿用户";
            model.AddManualUserCommand.Execute(null);
            model.ManualUid = "invalid";
            model.AddManualUserCommand.Execute(null);
            model.SearchText = "viewer";
            var pending = model.SearchAsync(false);
            search.Responses[0].SetResult(remoteIssue
                ? new() { Issues = [new() { Message = "remote error" }] }
                : new());
            await pending;
            var oldEditStatus = model.EditStatus;
            var oldSearchStatus = model.SearchStatus;

            Localization.SetLanguage("zh-CN");

            Assert.Equal(oldEditStatus, model.EditStatus);
            Assert.Equal(oldSearchStatus, model.SearchStatus);
            model.RefreshLocalization();
            Assert.Equal(Localization.Get("SpecialAttention.InvalidUid"), model.EditStatus);
            Assert.Equal(remoteIssue ? "remote error" : Localization.Get("SpecialAttention.NoSearchResults"), model.SearchStatus);
            Assert.Equal("viewer", model.SearchText);
            Assert.Equal("invalid", model.ManualUid);
            Assert.Equal("草稿用户", Assert.Single(model.Users).DisplayName);
            Assert.Empty(model.SearchResults);
        }
        finally
        {
            Localization.SetLanguage(previousLanguage);
        }
    }

    /// <summary>构造和手动添加均不联网，释放后禁止所有修改命令。</summary>
    [AvaloniaFact]
    public void ManualAdditionDoesNotSearchAndDisposalDisablesCommands()
    {
        var search = new ControlledUserSearchService();
        var model = new SpecialAttentionEditorViewModel(new(), search, () => null);
        model.ManualUid = "42";
        model.AddManualUserCommand.Execute(null);
        Assert.Empty(search.Requests);

        model.Dispose();

        Assert.False(model.AddManualUserCommand.CanExecute(null));
        Assert.False(model.SearchCommand.CanExecute(null));
        Assert.False(model.LoadMoreCommand.CanExecute(null));
        Assert.False(model.AddSearchedUserCommand.CanExecute(new UserSearchResult { UserUid = 73 }));
        Assert.False(model.RemoveUserCommand.CanExecute(model.Users[0]));
    }
}
