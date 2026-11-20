namespace Yohuke.DanmuPanel.Tests.Translation;

internal sealed class TranslationCacheTestTimeProvider : TimeProvider
{
    private long utcTicks = new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero).Ticks;

    public override DateTimeOffset GetUtcNow()
    {
        return new(Interlocked.Read(ref utcTicks), TimeSpan.Zero);
    }

    internal void Advance(TimeSpan elapsed)
    {
        Interlocked.Add(ref utcTicks, elapsed.Ticks);
    }
}
