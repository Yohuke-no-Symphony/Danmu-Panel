using System.Threading.Channels;
using Yohuke.DanmuPanel.Apis.Models.Bilibili;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services.Messages;
using Yohuke.DanmuPanel.Services.Translation;

namespace Yohuke.DanmuPanel.Services;

/// <summary>一个房间标签的监听生命周期，串行持久化接收消息。</summary>
public sealed class RoomReceptionSession : IAsyncDisposable
{
    private readonly RoomTabState room;
    private readonly string cookie;
    private readonly IMessageStore store;
    private readonly Func<PanelSettings> settings;
    private readonly ILiveDanmuSocket socket;
    private readonly IBilibiliDanmuHistoryService history;
    private readonly DanmuHistoryReconciler reconciler = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<MessageRecord> incoming;
    private Task? socketTask;
    private Task? writerTask;
    private Task? historyTask;
    private long generation;
    private bool storageFailed;
    private int terminalFailure;

    public event Action<MessageRecord>? Committed;
    public event Action<string>? StatusChanged;
    public event Action<Exception>? Failed;
    public event Action<Exception>? TerminalFailure;

    public bool HasFailed => Volatile.Read(ref terminalFailure) != 0;

    /// <summary>创建可注入网络连接与历史服务的监听会话。</summary>
    public RoomReceptionSession(RoomTabState room, string cookie, IMessageStore store, Func<PanelSettings> settings,
        ILiveDanmuSocketFactory sockets, IBilibiliDanmuHistoryService history, int receiveQueueCapacity = 4096)
    {
        this.room = room;
        this.cookie = cookie;
        this.store = store;
        this.settings = settings;
        this.history = history;
        incoming = Channel.CreateBounded<MessageRecord>(new BoundedChannelOptions(receiveQueueCapacity)
        {
            SingleReader = true, FullMode = BoundedChannelFullMode.Wait
        });
        socket = sockets.Create(long.Parse(room.RoomId, System.Globalization.CultureInfo.InvariantCulture), cookie);
        socket.DanmuReceived += OnDanmu;
        socket.GiftReceived += OnGift;
        socket.SuperChatReceived += OnSuperChat;
        socket.GuardPurchaseReceived += OnGuard;
        socket.ErrorReceived += OnSocketError;
        socket.Disconnected += OnDisconnected;
        socket.Recovered += OnRecovered;
    }

    /// <summary>恢复存储代次后开始监听及近期消息补拉。</summary>
    public async Task StartAsync()
    {
        generation = await store.GetGenerationAsync(room.Id);
        var recent = await store.LoadRecentAsync(room.Id, MessageKind.Danmu, 600, lifetime.Token);
        foreach (var record in recent.Reverse())
        {
            reconciler.AcceptLive(new()
            {
                RoomId = record.RoomId,
                MessageId = record.MessageId,
                Timestamp = record.HasSourceTimestamp ? record.Time.ToUnixTimeSeconds() : 0,
                Uid = record.UserUid,
                Content = record.Content
            });
        }
        writerTask = WriteIncomingAsync();
        socketTask = RunSocketAsync();
        historyTask = LoadHistoryAsync();
        if (!lifetime.IsCancellationRequested)
        {
            Notify(StatusChanged, "Panel.Listening");
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        if (socketTask is not null)
        {
            await socketTask;
        }
        if (historyTask is not null)
        {
            await historyTask;
        }
        incoming.Writer.TryComplete();
        if (writerTask is not null)
        {
            await writerTask;
        }
        socket.DanmuReceived -= OnDanmu;
        socket.GiftReceived -= OnGift;
        socket.SuperChatReceived -= OnSuperChat;
        socket.GuardPurchaseReceived -= OnGuard;
        socket.ErrorReceived -= OnSocketError;
        socket.Disconnected -= OnDisconnected;
        socket.Recovered -= OnRecovered;
        lifetime.Dispose();
    }

    private async Task RunSocketAsync()
    {
        try
        {
            await socket.StartAsync(lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StopReceiving(exception);
        }
    }

    private void Enqueue(MessageRecord record)
    {
        if (lifetime.IsCancellationRequested)
        {
            return;
        }
        if (!incoming.Writer.TryWrite(record))
        {
            StopReceiving(new IOException("Panel.ReceiveQueueFull"));
        }
    }

    private async Task WriteIncomingAsync()
    {
        await foreach (var record in incoming.Reader.ReadAllAsync())
        {
            if (storageFailed)
            {
                continue;
            }
            try
            {
                var current = settings();
                var fingerprint = current.Translation.Enabled && record.Kind is MessageKind.Danmu or MessageKind.SuperChat
                    ? TranslationRequestFactory.GetConfigurationFingerprint(current.Translation) : null;
                var result = await store.AppendAsync(record, current.Reading, fingerprint);
                if (result.Record is { } committed)
                {
                    Notify(Committed, committed);
                }
            }
            catch (Exception exception)
            {
                storageFailed = true;
                StopReceiving(exception);
            }
        }
    }

    private async Task LoadHistoryAsync()
    {
        try
        {
            var messages = await history.GetRecentAsync(room.RoomId, cookie, cancellationToken: lifetime.Token);
            foreach (var message in reconciler.AcceptHistory(messages))
            {
                Enqueue(RoomMessageMapper.Map(message, room.Id, generation, true));
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            NotifyFailure(exception);
        }
    }

    private void OnDanmu(object? sender, BilibiliDanmuMessage value)
    {
        if (reconciler.AcceptLive(value))
        {
            Enqueue(RoomMessageMapper.Map(value, room.Id, generation));
        }
    }

    private void OnGift(object? sender, BilibiliGiftMessage value)
    {
        Enqueue(RoomMessageMapper.Map(value, room.Id, generation));
    }

    private void OnSuperChat(object? sender, BilibiliSuperChatMessage value)
    {
        Enqueue(RoomMessageMapper.Map(value, room.Id, generation));
    }

    private void OnGuard(object? sender, BilibiliGuardPurchaseMessage value)
    {
        Enqueue(RoomMessageMapper.Map(value, room.Id, generation));
    }

    private void OnSocketError(object? sender, Exception exception)
    {
        NotifyFailure(exception);
    }

    private void OnDisconnected(object? sender, EventArgs args)
    {
        if (!lifetime.IsCancellationRequested)
        {
            Notify(StatusChanged, "Panel.Reconnecting");
        }
    }

    private void OnRecovered(object? sender, EventArgs args)
    {
        if (lifetime.IsCancellationRequested)
        {
            return;
        }
        Notify(StatusChanged, "Panel.Listening");
        if (historyTask?.IsCompleted != false)
        {
            historyTask = LoadHistoryAsync();
        }
    }

    private void StopReceiving(Exception error)
    {
        if (Interlocked.Exchange(ref terminalFailure, 1) != 0)
        {
            return;
        }
        lifetime.Cancel();
        NotifyFailure(error);
        Notify(TerminalFailure, error);
    }

    private void Notify<T>(Action<T>? observers, T value)
    {
        if (observers is null)
        {
            return;
        }
        foreach (var observer in observers.GetInvocationList().Cast<Action<T>>())
        {
            try
            {
                observer(value);
            }
            catch (Exception error)
            {
                NotifyFailure(error);
            }
        }
    }

    private void NotifyFailure(Exception error)
    {
        if (Failed is not { } observers)
        {
            return;
        }
        foreach (var observer in observers.GetInvocationList().Cast<Action<Exception>>())
        {
            try
            {
                observer(error);
            }
            catch (Exception observerError)
            {
                System.Diagnostics.Trace.TraceError("Message observer failed: {0}", observerError.GetType().Name);
            }
        }
    }
}
