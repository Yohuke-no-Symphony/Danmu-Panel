using System.Collections.Concurrent;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services.Messages;
using Yohuke.DanmuPanel.Services.Appearance;
using Yohuke.DanmuPanel.Services.Translation;

namespace Yohuke.DanmuPanel.Services;

/// <summary>组合监听、消息数据库及翻译调度，管理它们的退出顺序。</summary>
public sealed partial class PanelRuntime : IAsyncDisposable
{
    private readonly Dictionary<string, RoomReceptionSession> sessions = new();
    private readonly ConcurrentDictionary<string, string> statuses = new();
    private readonly HttpClient translationHttp = new();
    private readonly TranslationScheduler scheduler;
    private readonly TranslationService translationService;
    private readonly SqliteTranslationCache translationCache;
    private readonly Dictionary<string, string> translationCacheFingerprints;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private PanelSettings receptionSettings;
    public ConfigurationService ConfigurationStore { get; }
    public PanelConfiguration Configuration { get; }
    public SqliteMessageStore Messages { get; }
    public DanmuRegexFilter DanmuFilter { get; private set; }

    /// <summary>向当前阅读房间添加和清理仅用于界面验证的本地消息。</summary>
    public TestMessageService TestMessages { get; }
    public EmoticonCacheService Emoticons { get; }
    public AvatarCacheService Avatars { get; }
    /// <summary>按用户 UID 共享消息头像缓存与更新通知。</summary>
    public UserAvatarService UserAvatars { get; }
    public ColorSchemeService Colors { get; } = new();
    public BilibiliAccountSessionService AccountSessions { get; } = new();
    public IPinyinService Pinyin { get; }
    internal MessagePresentationService Presentations { get; }
    public event Action<string>? Changed;

    /// <summary>通知同类消息面板同步列宽，不刷新消息数据或翻译任务。</summary>
    public event Action<MessageKind>? MessageColumnsChanged;

    /// <summary>通知消息或译文变化；类型为空时重读该房间的所有消息面板。</summary>
    public event Action<string, MessageKind?>? MessagesChanged;

    /// <summary>组合应用依赖，不自动连接直播间。</summary>
    public PanelRuntime(ConfigurationService configurationStore, PanelConfiguration configuration, IPinyinService? pinyin = null)
    {
        Pinyin = pinyin ?? new PinyinService();
        Presentations = pinyin is null ? new(static () => new PinyinService()) : new(pinyin);
        ConfigurationStore = configurationStore;
        Configuration = configuration;
        TranslationRequestFactory.PreserveCacheIdentity(configuration.Settings.Translation);
        translationCacheFingerprints = configuration.Settings.Translation.CacheFingerprints;
        receptionSettings = CaptureReceptionSettings();
        DanmuFilter = CreateDanmuFilter();
        foreach (var account in configuration.Accounts)
        {
            AccountSessions.Register(account);
        }
        configuration.Accounts.CollectionChanged += OnAccountsChanged;
        var database = Path.Combine(configurationStore.DirectoryPath, "messages.db");
        Messages = new(database);
        translationCache = new(Path.Combine(configurationStore.DirectoryPath, "translation-cache.db"));
        translationService = new(new OpenAiCompatibleTranslationClient(translationHttp), translationCache);
        scheduler = new(Messages, translationService);
        TestMessages = new(Messages, () => Configuration.Settings, roomId => MessagesChanged?.Invoke(roomId, null),
            () => scheduler.NotifyWorkAvailable());
        scheduler.Completed += (_, args) => MessagesChanged?.Invoke(args.RoomTabId, args.Kind);
        scheduler.PendingChanged += (roomId, kind) => MessagesChanged?.Invoke(roomId, kind);
        scheduler.Error += message => ReportError(message, I18n.Localization.Get("Panel.Log.Translation"));
        Emoticons = new(new AppDirectoryService(configurationStore.DirectoryPath));
        Avatars = new(new AppDirectoryService(configurationStore.DirectoryPath));
        UserAvatars = new(new AppDirectoryService(configurationStore.DirectoryPath), Avatars);
    }

    /// <summary>应用保存的阅读规则并恢复翻译队列。</summary>
    public async Task InitializeAsync()
    {
        await Messages.ReevaluateReadOptionsAsync(Configuration.Settings.Reading);
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
        Configuration.Settings.Translation.CacheFingerprints = translationCacheFingerprints;
        var danmuFilter = CreateDanmuFilter();
        var nextReceptionSettings = CaptureReceptionSettings();
        await ConfigurationStore.SaveAsync(Configuration);
        await Messages.ReevaluateReadOptionsAsync(Configuration.Settings.Reading);
        await scheduler.UpdateSettingsAsync(Configuration.Settings.Translation);
        DanmuFilter = danmuFilter;
        Volatile.Write(ref receptionSettings, nextReceptionSettings);
        SpecialAttentionChanged?.Invoke();
        Changed?.Invoke("");
    }

    /// <summary>即时刷新消息的全局配色，不触发翻译或重新读取设置。</summary>
    public void NotifyAppearanceChanged()
    {
        Changed?.Invoke("");
    }

    /// <summary>在消息列宽调整后同步各宿主的显示比例。</summary>
    public void NotifyMessageColumnsChanged(MessageKind kind)
    {
        MessageColumnsChanged?.Invoke(kind);
    }

    /// <summary>在监听停止并排空后清空记录，完成后恢复原监听。</summary>
    public Task ClearAsync(RoomTabState? room)
    {
        return ClearMessagesAsync(room, null);
    }

    /// <summary>排空房间监听后仅清空指定面板，保留其他消息与翻译任务并恢复监听。</summary>
    public Task ClearFeedAsync(RoomTabState room, MessageKind kind)
    {
        return ClearMessagesAsync(room, kind);
    }

    private async Task ClearMessagesAsync(RoomTabState? room, MessageKind? kind)
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
            else if (kind is { } feedKind)
            {
                await Messages.ClearFeedAsync(room.Id, feedKind);
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
            await Presentations.DisposeAsync();
            await translationService.DisposeAsync();
            await translationCache.DisposeAsync();
            await ConfigurationStore.SaveAsync(Configuration);
            Emoticons.Dispose();
            await UserAvatars.DisposeAsync();
            Avatars.Dispose();
            Colors.Dispose();
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
        Volatile.Write(ref receptionSettings, CaptureReceptionSettings());
        var account = PanelAccountSelector.Resolve(Configuration, workspace, room);
        var session = new RoomReceptionSession(room, account?.Cookie ?? "", Messages, () => Volatile.Read(ref receptionSettings),
            new BilibiliLiveDanmuSocketFactory(), new BilibiliDanmuHistoryService());
        session.Committed += message =>
        {
            scheduler.NotifyWorkAvailable();
            MessagesChanged?.Invoke(message.RoomTabId, message.Kind);
        };
        session.StatusChanged += status =>
        {
            statuses[room.Id] = status;
            Changed?.Invoke(room.Id);
        };
        var logSource = I18n.Localization.Format("Panel.Log.Reception", room.Name, room.RoomId);
        session.Failed += exception => ReportError(exception.Message, logSource);
        session.TerminalFailure += exception =>
        {
            statuses[room.Id] = "Panel.ListenerFailed";
            ReportError(exception.Message, logSource);
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

    private PanelSettings CaptureReceptionSettings()
    {
        return new()
        {
            Reading = Configuration.Settings.Reading,
            Translation = TranslationRequestFactory.Snapshot(Configuration.Settings.Translation)
        };
    }

    private DanmuRegexFilter CreateDanmuFilter()
    {
        var filter = new DanmuRegexFilter(Configuration.Settings.DanmuRegexFilter);
        filter.PatternTimedOut += index => ReportError(
            I18n.Localization.Format("Settings.DanmuRegexTimedOut", index),
            I18n.Localization.Get("Settings.DanmuRegexFilter"));
        return filter;
    }

    private void ReportError(string message, string source)
    {
        var key = Configuration.Settings.Translation.ApiKey;
        StartupLog.Append(message, Models.Logging.AppLogLevel.ERROR, source, sensitiveValues: [key]);
    }
}
