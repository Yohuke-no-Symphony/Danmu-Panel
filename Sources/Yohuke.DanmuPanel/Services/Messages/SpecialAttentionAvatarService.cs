using System.Collections.Concurrent;
using System.Globalization;
using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.Services.Messages;

internal sealed class SpecialAttentionAvatarService(AvatarCacheService images, IUserAvatarService fallback) : IUserAvatarService
{
    private readonly ConcurrentDictionary<long, string> knownAvatars = new();
    private readonly ConcurrentDictionary<long, string> invalidAvatars = new();
    private event Action<long>? knownAvatarChanged;

    /// <inheritdoc />
    public event Action<long>? AvatarChanged
    {
        add
        {
            knownAvatarChanged += value;
            fallback.AvatarChanged += value;
        }
        remove
        {
            knownAvatarChanged -= value;
            fallback.AvatarChanged -= value;
        }
    }

    internal void Register(UserSearchResult result)
    {
        if (result.AvatarUrl.Length == 0 || knownAvatars.TryGetValue(result.UserUid, out var current) && current == result.AvatarUrl)
        {
            return;
        }
        knownAvatars[result.UserUid] = result.AvatarUrl;
        knownAvatarChanged?.Invoke(result.UserUid);
    }

    /// <inheritdoc />
    public async Task<string> GetAvatarAsync(long userUid, CancellationToken cancellationToken = default)
    {
        if (knownAvatars.TryGetValue(userUid, out var url) && invalidAvatars.GetValueOrDefault(userUid) != url)
        {
            var path = await images.DownloadAvatarAsync(userUid.ToString(CultureInfo.InvariantCulture), url, cancellationToken);
            if (path.Length > 0)
            {
                return path;
            }
        }
        return await fallback.GetAvatarAsync(userUid, cancellationToken);
    }

    /// <inheritdoc />
    public Task InvalidateAvatarAsync(long userUid, string invalidPath, CancellationToken cancellationToken = default)
    {
        if (knownAvatars.TryGetValue(userUid, out var url))
        {
            invalidAvatars[userUid] = url;
        }
        return fallback.InvalidateAvatarAsync(userUid, invalidPath, cancellationToken);
    }
}
