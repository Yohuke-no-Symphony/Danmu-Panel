using Yohuke.DanmuPanel.Services;

namespace Yohuke.DanmuPanel.Tests.Workspace;

internal sealed class ControlledUserAvatarService : IUserAvatarService
{
    private Action<long>? changed;

    internal Func<long, CancellationToken, Task<string>> Resolve { get; set; } = (_, _) => Task.FromResult("");
    internal List<long> RequestedUsers { get; } = [];
    internal int Subscribers { get; private set; }
    internal List<(long UserUid, string Path)> Invalidations { get; } = [];
    internal Action<long, string>? OnInvalidate { get; set; }
    internal Func<long, string, CancellationToken, Task> OnInvalidateAsync { get; set; } = (_, _, _) => Task.CompletedTask;

    public event Action<long>? AvatarChanged
    {
        add
        {
            changed += value;
            Subscribers++;
        }
        remove
        {
            changed -= value;
            Subscribers--;
        }
    }

    public Task<string> GetAvatarAsync(long userUid, CancellationToken cancellationToken = default)
    {
        RequestedUsers.Add(userUid);
        return Resolve(userUid, cancellationToken);
    }

    public Task InvalidateAvatarAsync(long userUid, string invalidPath, CancellationToken cancellationToken = default)
    {
        Invalidations.Add((userUid, invalidPath));
        OnInvalidate?.Invoke(userUid, invalidPath);
        return OnInvalidateAsync(userUid, invalidPath, cancellationToken);
    }

    internal void NotifyChanged(long userUid)
    {
        changed?.Invoke(userUid);
    }
}
