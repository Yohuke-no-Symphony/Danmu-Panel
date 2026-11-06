using System.Diagnostics;
using Avalonia.Threading;

namespace Yohuke.DanmuPanel.ViewModels;

internal sealed class FeedUiWorkBudget
{
    private const int MAX_UPDATES = 20;
    private const int MAX_WORK_MILLISECONDS = 4;
    private long startedAt = Stopwatch.GetTimestamp();
    private int updates;

    internal Task YieldIfNeededAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (++updates < MAX_UPDATES && Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds < MAX_WORK_MILLISECONDS)
        {
            return Task.CompletedTask;
        }
        return YieldToDispatcherAsync(cancellationToken);
    }

    private async Task YieldToDispatcherAsync(CancellationToken cancellationToken)
    {
        await Dispatcher.UIThread.InvokeAsync(static () =>
        {
        }, DispatcherPriority.Background);
        cancellationToken.ThrowIfCancellationRequested();
        updates %= MAX_UPDATES;
        startedAt = Stopwatch.GetTimestamp();
    }
}
