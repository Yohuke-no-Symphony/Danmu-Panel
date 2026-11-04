using System.Collections.Concurrent;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services.Messages;
using Yohuke.DanmuPanel.Services.Translation;

namespace Yohuke.DanmuPanel.Services;

/// <summary>组合监听、消息数据库及翻译调度，管理它们的退出顺序。</summary>
public sealed class PanelRuntime : IAsyncDisposable
{
    private readonly Dictionary<string, RoomReceptionSession> sessions = new();
    private readonly ConcurrentDictionary<string, string> statuses = new();
    private readonly HttpClient translationHttp = new();
    private readonly TranslationScheduler scheduler;
    private readonly TranslationService translationService;
    private readonly SqliteTranslationCache translationCache;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    public ConfigurationService ConfigurationStore { get; }
    public PanelConfiguration Configuration { get; }
    public SqliteMessageStore Messages { get; }

    /// <summary>向当前阅读房间添加和清理仅用于界面验证的本地消息。</summary>
    public TestMessageService TestMessages { get; }
    public EmoticonCacheService Emoticons { get; }
    public BilibiliAccountSessionService AccountSessions { get; } = new();
    public IPinyinService Pinyin { get; } = new PinyinService();
    public event Action<string>? Changed;
    public event Action<string>? Error;

    /// <summary>组合应用依赖，不自动连接直播间。</summary>
    public PanelRuntime(ConfigurationService configurationStore, PanelConfiguration configuration)
    {
        ConfigurationStore = configurationStore;
        Configuration = configuration;
        foreach (var account in configuration.Accounts)
        {
            AccountSessions.Register(account);
        }
        configuration.Accounts.CollectionChanged += OnAccountsChanged;
        var database = Path.Combine(configurationStore.DirectoryPath, "messages.db");
        Messages = new(database);
        TestMessages = new(Messages, () => Configuration.Settings, roomId => Changed?.Invoke(roomId));
        translationCache = new(Path.Combine(configurationStore.DirectoryPath, "translation-cache.db"));
        translationService = new(new OpenAiCompatibleTranslationClient(translationHttp), translationCache);
        scheduler = new(Messages, translationService);
        scheduler.Completed += (_, args) => Changed?.Invoke(args.RoomTabId);
        scheduler.Error += ReportError;
        Emoticons = new(new AppDirectoryService(configurationStore.DirectoryPath));
    }

    /// <summary>恢复翻译队列。</summary>
    public async Task InitializeAsync()
    {
        await scheduler.StartAsync(Configuration.Settings.Translation);
        await Task.WhenAll(Configuration.Accounts.Select(account => AccountSessions.ValidateAsync(account)));
    }

    /// <summary>读取房间监听状态。</summary>
    public bool IsListening(RoomTabState room)
    {
        return sessions.ContainsKey(room.Id);
    }

    /// <summary>读取可本地化的房间连接状态。</summary>
    public string GetStatus(RoomTabState? room)
    {
        return room is not null && statuses.TryGetValue(room.Id, out var status) ? status : "Panel.Stopped";
    }

    /// <summary>切换一个房间的监听，保留其他房间会话。</summary>
    public async Task ToggleListeningAsync(WorkspaceState workspace, RoomTabState room)
    {
        await lifecycle.WaitAsync();
        try
        {
            if (sessions.ContainsKey(room.Id))
            {
                await StopAsync(room.Id);
            }
            else
            {
                await StartAsync(workspace, room);
            }
        }
        finally
        {
            lifecycle.Release();
        }
    }

    /// <summary>保存设置并刷新未读优先级和翻译配置。</summary>
    public async Task ApplySettingsAsync()
    {
        await ConfigurationStore.SaveAsync(Configuration);
        await Messages.ReevaluateReadOptionsAsync(Configuration.Settings.Reading);
        await scheduler.UpdateSettingsAsync(Configuration.Settings.Translation);
        Changed?.Invoke("");
    }

    /// <summary>在监听停止并排空后清空记录，完成后恢复原监听。</summary>
    public async Task ClearAsync(RoomTabState? room)
    {
        await lifecycle.WaitAsync();
        try
        {
            var active = Configuration.Workspaces.SelectMany(workspace => workspace.Rooms
                .Where(tab => sessions.ContainsKey(tab.Id) && (room is null || tab.Id == room.Id))
                .Select(tab => (Workspace: workspace, Room: tab))).ToArray();
            foreach (var entry in active)
            {
                await StopAsync(entry.Room.Id);
            }
            if (room is null)
            {
                await Messages.ClearAllAsync();
            }
            else
            {
                await Messages.ClearRoomAsync(room.Id);
            }
            Changed?.Invoke(room?.Id ?? "");
            foreach (var entry in active)
            {
                await StartAsync(entry.Workspace, entry.Room);
            }
        }
        finally
        {
            lifecycle.Release();
        }
    }

    /// <summary>移除房间前停止对应监听。</summary>
    public Task StopRoomAsync(RoomTabState room)
    {
        return StopAsync(room.Id);
    }

    /// <summary>通知新建或重新排队的翻译任务。</summary>
    public void NotifyTranslation(bool retryAuthentication = false)
    {
        scheduler.NotifyWorkAvailable(retryAuthentication);
    }

    /// <summary>清空共享译文缓存，不修改已经保存的消息译文。</summary>
    public Task ClearTranslationCacheAsync()
    {
        return translationCache.ClearAsync();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await lifecycle.WaitAsync();
        try
        {
            foreach (var id in sessions.Keys.ToArray())
            {
                await StopAsync(id);
            }
            await scheduler.DisposeAsync();
            await translationService.DisposeAsync();
            await translationCache.DisposeAsync();
            await ConfigurationStore.SaveAsync(Configuration);
            Emoticons.Dispose();
            Configuration.Accounts.CollectionChanged -= OnAccountsChanged;
            AccountSessions.Dispose();
            await Messages.DisposeAsync();
            translationHttp.Dispose();
        }
        finally
        {
            lifecycle.Release();
        }
    }

    private void OnAccountsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
    {
        if (args.OldItems is not null)
        {
            foreach (BilibiliAccount account in args.OldItems)
            {
                AccountSessions.Remove(account.Id);
            }
        }
        if (args.NewItems is not null)
        {
            foreach (BilibiliAccount account in args.NewItems)
            {
                AccountSessions.Register(account);
            }
        }
    }

    private async Task StartAsync(WorkspaceState workspace, RoomTabState room)
    {
        var account = PanelAccountSelector.Resolve(Configuration, workspace, room);
        var session = new RoomReceptionSession(room, account?.Cookie ?? "", Messages, () => Configuration.Settings,
            new BilibiliLiveDanmuSocketFactory(), new BilibiliDanmuHistoryService());
        session.Committed += message =>
        {
            scheduler.NotifyWorkAvailable();
            Changed?.Invoke(message.RoomTabId);
        };
        session.StatusChanged += status =>
        {
            statuses[room.Id] = status;
            Changed?.Invoke(room.Id);
        };
        session.Failed += exception => ReportError(exception.Message);
        session.TerminalFailure += exception =>
        {
            statuses[room.Id] = "Panel.ListenerFailed";
            ReportError(exception.Message);
            Changed?.Invoke(room.Id);
        };
        sessions.Add(room.Id, session);
        try
        {
            await session.StartAsync();
        }
        catch
        {
            sessions.Remove(room.Id);
            await session.DisposeAsync();
            throw;
        }
    }

    private async Task StopAsync(string id)
    {
        if (sessions.Remove(id, out var session))
        {
            await session.DisposeAsync();
        }
        statuses[id] = "Panel.Stopped";
        Changed?.Invoke(id);
    }

    private void ReportError(string message)
    {
        var key = Configuration.Settings.Translation.ApiKey;
        var sanitized = AppLogSanitizer.Sanitize(string.IsNullOrEmpty(key) ? message : message.Replace(key, "[REDACTED]", StringComparison.Ordinal));
        StartupLog.Append(sanitized, Models.Logging.AppLogLevel.ERROR);
        Error?.Invoke(sanitized);
    }
}
