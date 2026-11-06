namespace Yohuke.DanmuPanel.Tests.Workspace;

internal sealed class UserAvatarTestTimeProvider : TimeProvider
{
    private readonly object syncRoot = new();
    private DateTimeOffset utcNow = new(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (syncRoot)
        {
            return utcNow;
        }
    }

    internal void Advance(TimeSpan elapsed)
    {
        lock (syncRoot)
        {
            utcNow += elapsed;
        }
    }
}
