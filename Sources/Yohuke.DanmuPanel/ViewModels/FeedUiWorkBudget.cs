using System.Diagnostics;
using Avalonia.Threading;

namespace Yohuke.DanmuPanel.ViewModels;

internal sealed class FeedUiWorkBudget
{
    private const int MAX_UPDATES = 20;
    private const int MAX_WORK_MILLISECONDS = 4;
    private const int FRAME_MILLISECONDS = 16;
    private long startedAt = Stopwatch.GetTimestamp();
    private int updates;

    internal Task YieldIfNeededAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (++updates < MAX_UPDATES && Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds < MAX_WORK_MILLISECONDS)
        {
            return Task.CompletedTask;
        }
        return WaitForFrameAsync(cancellationToken);
    }

    private async Task WaitForFrameAsync(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timer = DispatcherTimer.RunOnce(() => completion.TrySetResult(),
            TimeSpan.FromMilliseconds(FRAME_MILLISECONDS), DispatcherPriority.Background);
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        await completion.Task;
        cancellationToken.ThrowIfCancellationRequested();
        updates %= MAX_UPDATES;
        startedAt = Stopwatch.GetTimestamp();
    }
}
