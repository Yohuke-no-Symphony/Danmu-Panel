using System.Threading.Channels;
using Yohuke.DanmuPanel.Apis.Models.Bilibili;
using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.Services;

public sealed partial class RoomReceptionSession
{
    private const int RED_POCKET_RECOVERY_INTERVAL_SECONDS = 15;
    private readonly IBilibiliRedPocketService? redPockets;
    private readonly Channel<byte> redPocketRefresh = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropWrite
    });
    private Task? redPocketTask;
    private int refreshRedPocketList = 1;
    private string? lastRedPocketFailure;

    private async Task RecoverRedPocketsAsync(IBilibiliRedPocketService service)
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                try
                {
                    if (Interlocked.Exchange(ref refreshRedPocketList, 0) != 0)
                    {
                        await LoadActiveRedPocketsAsync(service).ConfigureAwait(false);
                    }
                    await LoadPendingRedPocketResultsAsync(service).ConfigureAwait(false);
                }
                catch (Exception exception) when (!lifetime.IsCancellationRequested)
                {
                    ReportRedPocketFailure(exception);
                }
                using var wakeup = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                wakeup.CancelAfter(TimeSpan.FromSeconds(RED_POCKET_RECOVERY_INTERVAL_SECONDS));
                try
                {
                    await redPocketRefresh.Reader.ReadAsync(wakeup.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
                {
                }
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

    private async Task LoadActiveRedPocketsAsync(IBilibiliRedPocketService service)
    {
        try
        {
            var messages = await service.GetActiveAsync(room.RoomId, cookie, cancellationToken: lifetime.Token)
                .ConfigureAwait(false);
            foreach (var message in messages)
            {
                var timestamp = message.RedPocket.StartTime > 0 ? message.RedPocket.StartTime : message.Timestamp;
                Enqueue(RoomMessageMapper.Map(message with { Timestamp = timestamp }, room.Id, generation, true) with
                {
                    HasSourceTimestamp = message.RedPocket.StartTime > 0
                });
            }
            lastRedPocketFailure = null;
        }
        catch (Exception exception) when (!lifetime.IsCancellationRequested)
        {
            Interlocked.Exchange(ref refreshRedPocketList, 1);
            ReportRedPocketFailure(exception);
        }
    }

    private async Task LoadPendingRedPocketResultsAsync(IBilibiliRedPocketService service)
    {
        var records = await store.LoadRecentAsync(room.Id, MessageKind.RedPocket, settings().MessageRetentionCount, lifetime.Token)
            .ConfigureAwait(false);
        foreach (var record in records)
        {
            if (record.RedPocket is not { } data || data.HasDetails && data.HasResult)
            {
                continue;
            }
            try
            {
                if (!data.HasDetails || data.EndTime == 0)
                {
                    var details = await service.GetInfoAsync(room.RoomId, data.LotId, cookie, cancellationToken: lifetime.Token)
                        .ConfigureAwait(false);
                    if (details is not null)
                    {
                        EnqueueRecoveredRedPocket(details, record);
                        data = details.RedPocket with { HasResult = data.HasResult };
                    }
                }
                var drawTime = data.EndTime > 0 ? data.EndTime : data.StartTime;
                if (!data.HasResult && drawTime <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                {
                    var result = await service.GetWinnersAsync(room.RoomId, data.LotId, cookie, cancellationToken: lifetime.Token)
                        .ConfigureAwait(false);
                    if (result is not null)
                    {
                        EnqueueRecoveredRedPocket(result, record);
                    }
                }
                lastRedPocketFailure = null;
            }
            catch (Exception exception) when (!lifetime.IsCancellationRequested)
            {
                ReportRedPocketFailure(exception);
            }
        }
    }

    private void EnqueueRecoveredRedPocket(BilibiliRedPocketMessage message, MessageRecord previous)
    {
        Enqueue(RoomMessageMapper.Map(message, room.Id, generation, true) with
        {
            Time = previous.Time,
            HasSourceTimestamp = previous.HasSourceTimestamp
        });
    }

    private void ReportRedPocketFailure(Exception exception)
    {
        if (lastRedPocketFailure != exception.Message)
        {
            lastRedPocketFailure = exception.Message;
            NotifyFailure(exception);
        }
    }
}
