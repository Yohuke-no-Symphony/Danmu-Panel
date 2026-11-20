using Avalonia.Threading;
using Yohuke.DanmuPanel.Services;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed class UserAvatarThumbnailCache(IUserAvatarService source)
{
    private const int MAX_THUMBNAILS = 128;
    private const long MAX_RETAINED_BYTES = 8 * 1024 * 1024;
    private readonly Dictionary<long, LinkedListNode<(long UserUid, UserAvatarThumbnail Thumbnail)>> entries = [];
    private readonly LinkedList<(long UserUid, UserAvatarThumbnail Thumbnail)> recency = [];
    private long retainedBytes;

    internal IUserAvatarService Source { get; } = source;

    internal UserAvatarThumbnail? Find(long userUid, string? path = null)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!entries.TryGetValue(userUid, out var node) || path is not null && node.Value.Thumbnail.Path != path)
        {
            return null;
        }
        recency.Remove(node);
        recency.AddFirst(node);
        return node.Value.Thumbnail;
    }

    internal void Store(long userUid, UserAvatarThumbnail thumbnail)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (thumbnail.ByteSize > MAX_RETAINED_BYTES)
        {
            return;
        }
        thumbnail.Retain();
        if (entries.TryGetValue(userUid, out var previous))
        {
            Remove(previous);
        }
        entries.Add(userUid, recency.AddFirst((userUid, thumbnail)));
        retainedBytes += thumbnail.ByteSize;
        while (entries.Count > MAX_THUMBNAILS || retainedBytes > MAX_RETAINED_BYTES)
        {
            Remove(recency.Last!);
        }
    }

    internal void Clear()
    {
        Dispatcher.UIThread.VerifyAccess();
        while (recency.Last is { } node)
        {
            Remove(node);
        }
    }

    private void Remove(LinkedListNode<(long UserUid, UserAvatarThumbnail Thumbnail)> node)
    {
        var (userUid, thumbnail) = node.Value;
        entries.Remove(userUid);
        recency.Remove(node);
        retainedBytes -= thumbnail.ByteSize;
        thumbnail.Release();
    }
}
