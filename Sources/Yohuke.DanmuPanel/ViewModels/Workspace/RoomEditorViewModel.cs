using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Models.Workspace;
using Yohuke.DanmuPanel.Services;

namespace Yohuke.DanmuPanel.ViewModels.Workspace;

/// <summary>加房弹窗中的搜索、分页和手动资料草稿。</summary>
public sealed partial class RoomEditorViewModel : ObservableObject, IDisposable
{
    private readonly PanelRuntime runtime;
    private readonly WorkspaceState workspace;
    private readonly RoomTabState room;
    private readonly Action<bool> close;
    private readonly CancellationToken cancellationToken;
    private readonly IBilibiliLiveRoomSearchService searchService;
    private CancellationTokenSource? currentSearch;
    private long searchVersion;
    private bool searching;
    private bool disposed;
    private int page;
    private string searchQuery = "";
    private IReadOnlyList<LiveRoomSearchIssue> searchIssues = [];
    private Exception? searchError;
    private string? searchStatusKey;
    [ObservableProperty] private string liveRoomSearchText = "";
    [ObservableProperty] private string liveRoomSearchStatus = "";
    [ObservableProperty] private bool hasMoreLiveRoomSearchResults;
    [ObservableProperty] private string newRoomName;
    [ObservableProperty] private string newRoomId;
    [ObservableProperty] private string newRoomOwnerUid;
    [ObservableProperty] private AccountChoice? selectedAccount;
    public ObservableCollection<LiveRoomSearchResultViewModel> LiveRoomSearchResults { get; } = [];
    public ObservableCollection<AccountChoice> AccountChoices { get; }
    public bool IsExisting { get; }
    public string AddDialogTitle => Localization.Get(IsExisting ? "Panel.EditRoom" : "Panel.AddRoom");
    public string AddDialogDescription => Localization.Get("Workspace.RoomSearchDescription");
    public string AddDialogWorkspaceName => workspace.Name;
    public string AddDialogPrimaryText => Localization.Get(IsExisting ? "Panel.Save" : "Workspace.Add");
    public IAsyncRelayCommand SearchLiveRoomsCommand { get; }
    public IAsyncRelayCommand LoadMoreLiveRoomsCommand { get; }
    public IRelayCommand<LiveRoomSearchResultViewModel> AddSearchedRoomCommand { get; }
    public IRelayCommand ConfirmCommand { get; }
    public IRelayCommand CancelCommand { get; }

    /// <summary>创建草稿而不触发网络，确认后才写回房间对象。</summary>
    public RoomEditorViewModel(PanelRuntime runtime, WorkspaceState workspace, RoomTabState room,
        bool existing, Action<bool> close, CancellationToken cancellationToken,
        IBilibiliLiveRoomSearchService? searchService = null)
    {
        this.runtime = runtime;
        this.workspace = workspace;
        this.room = room;
        this.close = close;
        this.cancellationToken = cancellationToken;
        this.searchService = searchService ?? new BilibiliLiveRoomSearchService();
        IsExisting = existing;
        newRoomName = room.Name;
        newRoomId = room.RoomId;
        newRoomOwnerUid = room.OwnerUid;
        AccountChoices = new(new[] { new AccountChoice(null, Localization.Get("Panel.InheritAccount")) }
            .Concat(runtime.Configuration.Accounts.Select(value => new AccountChoice(value.Id, value.Name))));
        selectedAccount = AccountChoices.FirstOrDefault(value => value.Id == room.AccountOverrideId) ?? AccountChoices[0];
        SearchLiveRoomsCommand = new AsyncRelayCommand(() => SearchAsync(false), () => !searching && !disposed);
        LoadMoreLiveRoomsCommand = new AsyncRelayCommand(() => SearchAsync(true), () => !searching && !disposed && HasMoreLiveRoomSearchResults);
        AddSearchedRoomCommand = new RelayCommand<LiveRoomSearchResultViewModel>(result =>
        {
            if (result is not null && !IsExisting)
            {
                NewRoomId = result.RoomId;
                NewRoomName = result.OwnerName;
                NewRoomOwnerUid = result.OwnerUid;
                Confirm();
            }
        });
        ConfirmCommand = new RelayCommand(Confirm);
        CancelCommand = new RelayCommand(() => close(false));
        Localization.LanguageChanged += OnLanguageChanged;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        searchVersion++;
        currentSearch?.Cancel();
        currentSearch = null;
        Localization.LanguageChanged -= OnLanguageChanged;
        RefreshCommands();
    }

    internal async Task SearchAsync(bool next)
    {
        if (disposed)
        {
            return;
        }
        var version = ++searchVersion;
        currentSearch?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        currentSearch = request;
        searching = true;
        RefreshCommands();
        try
        {
            var requestedPage = next ? page + 1 : 1;
            var query = next ? searchQuery : LiveRoomSearchText.Trim();
            var accountRoom = new RoomTabState { AccountOverrideId = SelectedAccount?.Id };
            var chosen = PanelAccountSelector.Resolve(runtime.Configuration, workspace, accountRoom);
            var response = await searchService.SearchAsync(query, requestedPage,
                chosen?.Cookie, cancellationToken: request.Token);
            if (version != searchVersion || disposed)
            {
                return;
            }
            request.Token.ThrowIfCancellationRequested();
            if (!next)
            {
                LiveRoomSearchResults.Clear();
                searchQuery = query;
            }
            foreach (var result in response.Results)
            {
                if (LiveRoomSearchResults.All(existing => existing.RoomId != result.RoomId))
                {
                    LiveRoomSearchResults.Add(new(result));
                }
            }
            page = requestedPage;
            HasMoreLiveRoomSearchResults = response.HasMore;
            searchIssues = response.Issues;
            searchError = null;
            searchStatusKey = searchIssues.Count == 0 && LiveRoomSearchResults.Count == 0
                ? "Workspace.NoSearchResults" : null;
            RefreshSearchStatus();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (version == searchVersion && !disposed)
            {
                searchIssues = [];
                searchStatusKey = null;
                searchError = exception;
                RefreshSearchStatus();
            }
        }
        finally
        {
            if (version == searchVersion)
            {
                searching = false;
                currentSearch = null;
                RefreshCommands();
            }
        }
    }

    private void RefreshCommands()
    {
        SearchLiveRoomsCommand.NotifyCanExecuteChanged();
        LoadMoreLiveRoomsCommand.NotifyCanExecuteChanged();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(AddDialogTitle));
        OnPropertyChanged(nameof(AddDialogDescription));
        OnPropertyChanged(nameof(AddDialogPrimaryText));
        var inherited = SelectedAccount?.Id is null;
        AccountChoices[0] = new(null, Localization.Get("Panel.InheritAccount"));
        if (inherited)
        {
            SelectedAccount = AccountChoices[0];
        }
        foreach (var result in LiveRoomSearchResults)
        {
            result.RefreshLocalization();
        }
        RefreshSearchStatus();
    }

    private void RefreshSearchStatus()
    {
        LiveRoomSearchStatus = searchStatusKey is { } key ? Localization.Get(key)
            : searchError is { } error ? LocalizedExceptionText.Get(error)
            : string.Join(Environment.NewLine, searchIssues.Select(issue =>
                issue.Error is { } info ? CoreErrorText.Get(info) : issue.Message));
    }

    private void Confirm()
    {
        var roomId = IsExisting ? room.RoomId : NewRoomId.Trim();
        if (!WorkspaceShareService.IsPositiveId(roomId))
        {
            searchStatusKey = "Room.InvalidRoomId";
            RefreshSearchStatus();
            return;
        }
        if (!string.IsNullOrWhiteSpace(NewRoomOwnerUid) && !WorkspaceShareService.IsPositiveId(NewRoomOwnerUid.Trim()))
        {
            searchStatusKey = "Workspace.InvalidOwnerUid";
            RefreshSearchStatus();
            return;
        }
        room.RoomId = roomId;
        room.Name = string.IsNullOrWhiteSpace(NewRoomName) ? room.RoomId : NewRoomName.Trim();
        room.OwnerUid = NewRoomOwnerUid.Trim();
        room.AccountOverrideId = SelectedAccount?.Id;
        close(true);
    }
}
