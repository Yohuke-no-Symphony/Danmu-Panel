using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services.Translation;

namespace Yohuke.DanmuPanel.Tests.Integration;

internal sealed class BlockingPinyinService : IPinyinService
{
    private readonly PinyinService inner = new();
    private int activeCalls;
    private int peakActiveCalls;
    internal int ActiveCalls => Volatile.Read(ref activeCalls);
    internal int PeakActiveCalls => Volatile.Read(ref peakActiveCalls);
    internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string GetPinyin(string text)
    {
        Wait();
        return inner.GetPinyin(text);
    }

    public IReadOnlyList<PinyinAnnotation> GetAnnotations(string text)
    {
        Wait();
        return inner.GetAnnotations(text);
    }

    private void Wait()
    {
        var current = Interlocked.Increment(ref activeCalls);
        var peak = Volatile.Read(ref peakActiveCalls);
        while (current > peak)
        {
            var observed = Interlocked.CompareExchange(ref peakActiveCalls, current, peak);
            if (observed == peak)
            {
                break;
            }
            peak = observed;
        }
        Entered.TrySetResult();
        try
        {
            Release.Task.GetAwaiter().GetResult();
        }
        finally
        {
            Interlocked.Decrement(ref activeCalls);
        }
    }
}
