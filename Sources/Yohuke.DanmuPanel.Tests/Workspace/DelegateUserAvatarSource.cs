using Yohuke.DanmuPanel.Services;

namespace Yohuke.DanmuPanel.Tests.Workspace;

internal sealed class DelegateUserAvatarSource(Func<long, CancellationToken, Task<string>> resolve)
    : IUserAvatarSource, IDisposable
{
    private int calls;

    internal int Calls => Volatile.Read(ref calls);
    internal bool IsDisposed { get; private set; }

    public Task<string> GetAvatarUrlAsync(long userUid, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref calls);
        return resolve(userUid, cancellationToken);
    }

    public void Dispose()
    {
        IsDisposed = true;
    }
}
