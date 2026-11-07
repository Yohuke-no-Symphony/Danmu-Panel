using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Services;

namespace Yohuke.DanmuPanel.ViewModels.Settings;

/// <summary>管理特别关注的独立名单草稿、手动添加及可取消的用户搜索。</summary>
public sealed partial class SpecialAttentionEditorViewModel : ObservableObject, IDisposable
{
    private readonly IBilibiliUserSearchService searchService;
    private readonly Func<string?> getCookie;
    private readonly HashSet<long> initialUserUids;
    private CancellationTokenSource? currentSearch;
    private IReadOnlyList<UserSearchIssue> searchIssues = [];
    private long searchVersion;
    private int page;
    private string searchQuery = "";
    private string? searchMessageKey;
    private string? editMessageKey;
    private bool disposed;
    private IUserAvatarService? avatarService;
    private Action<UserSearchResult>? registerAvatar;
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private string searchStatus = "";
    [ObservableProperty] private string editStatus = "";
    [ObservableProperty] private string manualUid = "";
    [ObservableProperty] private string manualDisplayName = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    private bool isSearching;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    private bool hasMoreResults;

    public ObservableCollection<SpecialAttentionUser> Users { get; }
    public ObservableCollection<UserSearchResult> SearchResults { get; } = [];
    public ObservableCollection<SpecialAttentionUserRowViewModel> UserRows { get; } = [];
    public ObservableCollection<SpecialAttentionUserRowViewModel> SearchResultRows { get; } = [];
    public IAsyncRelayCommand SearchCommand { get; }
    public IAsyncRelayCommand LoadMoreCommand { get; }
    public IRelayCommand<UserSearchResult> AddSearchedUserCommand { get; }
    public IRelayCommand AddManualUserCommand { get; }
    public IRelayCommand<SpecialAttentionUser> RemoveUserCommand { get; }

    /// <summary>复制初始名单作为草稿，不在构造期间发起查询或修改配置。</summary>
    public SpecialAttentionEditorViewModel(SpecialAttentionSettings initial, IBilibiliUserSearchService search,
        Func<string?> getCookie)
    {
        searchService = search;
        this.getCookie = getCookie;
        initialUserUids = initial.Users.Select(user => user.UserUid).ToHashSet();
        Users = new(initial.Users.Select(CloneUser));
        SearchCommand = new AsyncRelayCommand(() => SearchAsync(false),
            () => !disposed && !string.IsNullOrWhiteSpace(SearchText), AsyncRelayCommandOptions.AllowConcurrentExecutions);
        LoadMoreCommand = new AsyncRelayCommand(() => SearchAsync(true), () => !disposed && !IsSearching && HasMoreResults);
        AddSearchedUserCommand = new RelayCommand<UserSearchResult>(AddSearchedUser, CanAddSearchedUser);
        AddManualUserCommand = new RelayCommand(AddManualUser, () => !disposed);
        RemoveUserCommand = new RelayCommand<SpecialAttentionUser>(RemoveUser,
            user => !disposed && user is not null && Users.Any(existing => existing.UserUid == user.UserUid));
        Users.CollectionChanged += OnUsersChanged;
        SearchResults.CollectionChanged += OnSearchResultsChanged;
        SynchronizeUserRows();
    }

    /// <summary>复制草稿；提供当前配置时合并外部新增及删除，保留当前名称资料。</summary>
    public SpecialAttentionSettings CreateSnapshot(SpecialAttentionSettings? current = null)
    {
        if (current is null)
        {
            return new() { Users = Users.Select(CloneUser).ToList() };
        }
        var draftUids = Users.Select(user => user.UserUid).ToHashSet();
        var merged = current.Users
            .Where(user => !initialUserUids.Contains(user.UserUid) || draftUids.Contains(user.UserUid))
            .Select(CloneUser).ToList();
        var mergedUids = merged.Select(user => user.UserUid).ToHashSet();
        foreach (var user in Users)
        {
            if (!initialUserUids.Contains(user.UserUid) && mergedUids.Add(user.UserUid))
            {
                merged.Add(CloneUser(user));
            }
        }
        return new() { Users = merged };
    }

    /// <summary>取消当前请求并拒绝迟到结果，保留已显示的查询页及名单草稿。</summary>
    public void CancelSearch()
    {
        searchVersion++;
        var previous = currentSearch;
        currentSearch = null;
        previous?.Cancel();
        IsSearching = false;
        if (searchMessageKey == "SpecialAttention.Searching")
        {
            SetSearchStatus(null);
        }
        RefreshCommands();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        CancelSearch();
        Users.CollectionChanged -= OnUsersChanged;
        SearchResults.CollectionChanged -= OnSearchResultsChanged;
    }

    internal async Task SearchAsync(bool next)
    {
        if (disposed || next && (IsSearching || !HasMoreResults))
        {
            return;
        }
        CancelSearch();
        var query = next ? searchQuery : SearchText.Trim();
        var requestedPage = next ? page + 1 : 1;
        if (!next)
        {
            SearchResults.Clear();
            searchQuery = query;
            page = 0;
            HasMoreResults = false;
        }
        if (query.Length == 0)
        {
            SetSearchStatus(null);
            return;
        }
        var version = searchVersion;
        using var request = new CancellationTokenSource();
        currentSearch = request;
        IsSearching = true;
        SetSearchStatus("SpecialAttention.Searching");
        try
        {
            var response = await searchService.SearchAsync(query, requestedPage, getCookie(), cancellationToken: request.Token);
            if (version != searchVersion || disposed || request.IsCancellationRequested)
            {
                return;
            }
            if (response.Issues.Count > 0)
            {
                SetSearchStatus(null, response.Issues);
                return;
            }
            var existingUids = SearchResults.Select(result => result.UserUid).ToHashSet();
            foreach (var result in response.Results)
            {
                if (existingUids.Add(result.UserUid))
                {
                    SearchResults.Add(result);
                }
            }
            page = requestedPage;
            HasMoreResults = response.HasMore;
            SetSearchStatus(SearchResults.Count == 0 ? "SpecialAttention.NoSearchResults" : null);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (version == searchVersion && !disposed)
            {
                SetSearchStatus("Services.BilibiliUserSearch.SearchFailed");
            }
        }
        finally
        {
            if (version == searchVersion)
            {
                currentSearch = null;
                IsSearching = false;
            }
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        CancelSearch();
        SearchResults.Clear();
        searchQuery = value.Trim();
        page = 0;
        HasMoreResults = false;
        SetSearchStatus(null);
    }

    private bool CanAddSearchedUser(UserSearchResult? result)
    {
        return !disposed && result is { UserUid: > 0 } && Users.All(user => user.UserUid != result.UserUid);
    }

    private void AddSearchedUser(UserSearchResult? result)
    {
        if (disposed || result is not { UserUid: > 0 })
        {
            return;
        }
        if (Users.Any(user => user.UserUid == result.UserUid))
        {
            SetEditStatus("SpecialAttention.AlreadyAdded");
            return;
        }
        Users.Add(new() { UserUid = result.UserUid, DisplayName = result.DisplayName.Trim() });
        SetEditStatus(null);
    }

    private void AddManualUser()
    {
        if (disposed)
        {
            return;
        }
        var uidText = ManualUid.Trim();
        if (uidText.Length == 0 || !uidText.All(char.IsAsciiDigit) ||
            !long.TryParse(uidText, NumberStyles.None, CultureInfo.InvariantCulture, out var uid) || uid <= 0)
        {
            SetEditStatus("SpecialAttention.InvalidUid");
            return;
        }
        if (Users.Any(user => user.UserUid == uid))
        {
            SetEditStatus("SpecialAttention.AlreadyAdded");
            return;
        }
        Users.Add(new() { UserUid = uid, DisplayName = ManualDisplayName.Trim() });
        ManualUid = "";
        ManualDisplayName = "";
        SetEditStatus(null);
    }

    private void RemoveUser(SpecialAttentionUser? user)
    {
        if (disposed || user is null)
        {
            return;
        }
        var existing = Users.FirstOrDefault(value => value.UserUid == user.UserUid);
        if (existing is not null)
        {
            Users.Remove(existing);
            SetEditStatus(null);
        }
    }

    private void OnUsersChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        SynchronizeUserRows();
        AddSearchedUserCommand.NotifyCanExecuteChanged();
        RemoveUserCommand.NotifyCanExecuteChanged();
    }

    internal void ConfigureAvatars(IUserAvatarService service, Action<UserSearchResult> register)
    {
        avatarService = service;
        registerAvatar = register;
        UserRows.Clear();
        SearchResultRows.Clear();
        SynchronizeUserRows();
        SynchronizeSearchRows();
    }

    private void OnSearchResultsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        SynchronizeSearchRows();
    }

    private void SynchronizeUserRows()
    {
        var existing = UserRows.ToDictionary(row => row.Item);
        SynchronizeRows(UserRows, Users.Select(user => existing.GetValueOrDefault(user) ??
            new SpecialAttentionUserRowViewModel(user.UserUid, user.DisplayName, user, RemoveUserCommand, false, avatarService)).ToArray());
    }

    private void SynchronizeSearchRows()
    {
        var existing = SearchResultRows.ToDictionary(row => row.Item);
        var rows = SearchResults.Select(result =>
        {
            registerAvatar?.Invoke(result);
            return existing.GetValueOrDefault(result) ?? new SpecialAttentionUserRowViewModel(result.UserUid,
                result.DisplayName, result, AddSearchedUserCommand, true, avatarService);
        }).ToArray();
        SynchronizeRows(SearchResultRows, rows);
    }

    private static void SynchronizeRows(ObservableCollection<SpecialAttentionUserRowViewModel> target,
        IReadOnlyList<SpecialAttentionUserRowViewModel> rows)
    {
        var current = rows.ToHashSet();
        for (var index = target.Count - 1; index >= 0; index--)
        {
            if (!current.Contains(target[index]))
            {
                target.RemoveAt(index);
            }
        }
        for (var index = 0; index < rows.Count; index++)
        {
            var previous = target.IndexOf(rows[index]);
            if (previous < 0)
            {
                target.Insert(index, rows[index]);
            }
            else if (previous != index)
            {
                target.Move(previous, index);
            }
        }
    }

    private void RefreshCommands()
    {
        SearchCommand.NotifyCanExecuteChanged();
        LoadMoreCommand.NotifyCanExecuteChanged();
        AddSearchedUserCommand.NotifyCanExecuteChanged();
        AddManualUserCommand.NotifyCanExecuteChanged();
        RemoveUserCommand.NotifyCanExecuteChanged();
    }

    private void SetSearchStatus(string? key, IReadOnlyList<UserSearchIssue>? issues = null)
    {
        searchMessageKey = key;
        searchIssues = issues ?? [];
        RefreshSearchStatus();
    }

    private void RefreshSearchStatus()
    {
        SearchStatus = searchMessageKey is { } key
            ? Localization.Get(key)
            : string.Join(Environment.NewLine, searchIssues.Select(issue => issue.Message));
    }

    private void SetEditStatus(string? key)
    {
        editMessageKey = key;
        EditStatus = key is null ? "" : Localization.Get(key);
    }

    internal void RefreshLocalization()
    {
        RefreshSearchStatus();
        EditStatus = editMessageKey is null ? "" : Localization.Get(editMessageKey);
    }

    private static SpecialAttentionUser CloneUser(SpecialAttentionUser user)
    {
        return new() { UserUid = user.UserUid, DisplayName = user.DisplayName };
    }
}
