using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.Services.Translation;

/// <summary>以有限并发执行持久任务，保留 SC 和置顶消息的优先级。</summary>
public sealed class TranslationScheduler : ITranslationScheduler
{
    private const int MAX_CONCURRENCY = 4;
    private const int MAX_ORDINARY_PENDING = 200;
    private static readonly TimeSpan MAX_ORDINARY_AGE = TimeSpan.FromSeconds(30);
    private readonly ITranslationJobStore store;
    private readonly TranslationService service;
    private readonly SemaphoreSlim signal = new(0, 1);
    private readonly SemaphoreSlim configurationGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, Task> running = new(StringComparer.Ordinal);
    private readonly TimeProvider timeProvider;
    private TranslationSettings settings = new();
    private CancellationTokenSource generation = new();
    private Task? worker;
    private volatile bool authenticationFailed;
    private volatile bool disposed;
    private bool configured;

    /// <summary>创建调度器；服务和存储生命周期由应用负责。</summary>
    public TranslationScheduler(ITranslationJobStore store, TranslationService service, TimeProvider? timeProvider = null)
    {
        this.store = store;
        this.service = service;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event EventHandler<TranslationCompletedEventArgs>? Completed;
    public event Action<string>? Error;
    public event Action<string, MessageKind?>? PendingChanged;

    /// <summary>恢复待办并启动唯一调度循环。</summary>
    public async Task StartAsync(TranslationSettings settings, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (worker is not null)
        {
            throw new InvalidOperationException("Translation scheduler has already started.");
        }

        await UpdateSettingsAsync(settings, cancellationToken).ConfigureAwait(false);
        worker = RunAsync();
    }

    /// <summary>原子替换配置快照，取消旧配置任务并更新持久待办。</summary>
    public async Task UpdateSettingsAsync(TranslationSettings value, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = TranslationRequestFactory.Snapshot(value);
            var fingerprint = TranslationRequestFactory.GetConfigurationFingerprint(snapshot);
            var requestChanged = !configured || fingerprint != TranslationRequestFactory.GetConfigurationFingerprint(settings) ||
                snapshot.ApiKey != settings.ApiKey || snapshot.Enabled != settings.Enabled;
            if (requestChanged)
            {
                await generation.CancelAsync().ConfigureAwait(false);
                generation.Dispose();
                generation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                if (snapshot.Enabled)
                {
                    await store.ResetPendingAsync(fingerprint, cancellationToken).ConfigureAwait(false);
                }
                authenticationFailed = false;
            }

            settings = snapshot;
            configured = true;
        }
        finally
        {
            configurationGate.Release();
        }

        NotifyWorkAvailable();
    }

    /// <summary>通知新消息已创建持久任务。</summary>
    public void NotifyWorkAvailable(bool retryAuthentication = false)
    {
        if (disposed)
        {
            return;
        }

        if (retryAuthentication)
        {
            authenticationFailed = false;
        }

        try
        {
            signal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    /// <summary>取消网络等待，结束调度；未完成任务保留供下次启动。</summary>
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await lifetime.CancelAsync().ConfigureAwait(false);
        await generation.CancelAsync().ConfigureAwait(false);
        if (worker is not null)
        {
            await worker.ConfigureAwait(false);
        }

        await Task.WhenAll(running.Values).ConfigureAwait(false);
        generation.Dispose();
        lifetime.Dispose();
    }

    private async Task RunAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                foreach (var id in running.Where(pair => pair.Value.IsCompleted).Select(pair => pair.Key).ToArray())
                {
                    running.Remove(id);
                }

                await configurationGate.WaitAsync(lifetime.Token).ConfigureAwait(false);
                try
                {
                    if (settings.Enabled && !authenticationFailed && running.Count < MAX_CONCURRENCY)
                    {
                        if (settings.BacklogMode == TranslationBacklogMode.Realtime)
                        {
                            var trimmed = await store.TrimPendingAsync(timeProvider.GetUtcNow() - MAX_ORDINARY_AGE,
                                MAX_ORDINARY_PENDING, running.Keys.ToArray(), lifetime.Token).ConfigureAwait(false);
                            if (trimmed > 0)
                            {
                                PendingChanged?.Invoke("", null);
                            }
                        }

                        var pending = await store.GetPendingAsync(MAX_ORDINARY_PENDING + MAX_CONCURRENCY, lifetime.Token).ConfigureAwait(false);
                        var fingerprint = TranslationRequestFactory.GetConfigurationFingerprint(settings);
                        foreach (var job in pending)
                        {
                            if (running.Count >= MAX_CONCURRENCY)
                            {
                                break;
                            }

                            if (!running.ContainsKey(job.Id) && job.ConfigurationFingerprint == fingerprint)
                            {
                                running.Add(job.Id, ExecuteAsync(job, settings, generation.Token));
                            }
                        }
                    }
                }
                finally
                {
                    configurationGate.Release();
                }

                await signal.WaitAsync(TimeSpan.FromMilliseconds(500), lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                Error?.Invoke("translation.storage_error:" + exception.GetType().Name);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private async Task ExecuteAsync(TranslationJob job, TranslationSettings snapshot, CancellationToken cancellationToken)
    {
        try
        {
            var request = TranslationRequestFactory.Create(snapshot, job.Text, job.EmoticonTexts);
            if (request is null)
            {
                if (await store.FailAsync(job, "skipped:emoticon", cancellationToken).ConfigureAwait(false))
                {
                    PendingChanged?.Invoke(job.RoomTabId, job.Kind);
                }
                return;
            }

            var translation = await service.TranslateAsync(request, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (await store.CompleteAsync(job, translation, cancellationToken).ConfigureAwait(false))
            {
                Completed?.Invoke(this, new(job, translation));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var message = exception is TranslationException ? exception.Message : "translation.failed:" + exception.GetType().Name;
            if (message is "translation.http_error:401" or "translation.http_error:403")
            {
                authenticationFailed = true;
            }

            try
            {
                if (await store.FailAsync(job, message, cancellationToken).ConfigureAwait(false))
                {
                    PendingChanged?.Invoke(job.RoomTabId, job.Kind);
                    Error?.Invoke(message);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception storageException)
            {
                Error?.Invoke("translation.storage_error:" + storageException.GetType().Name);
            }
        }
        finally
        {
            NotifyWorkAvailable();
        }
    }
}
