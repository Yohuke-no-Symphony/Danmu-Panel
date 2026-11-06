namespace Yohuke.DanmuPanel.Models.Workspace;

internal sealed record UserAvatarCacheEntry
{
    public string AvatarUrl { get; init; } = "";
    public DateTimeOffset LastSuccessAt { get; init; }
    public DateTimeOffset LastCheckedAt { get; init; }
    public bool LastCheckSucceeded { get; init; }
    public string LastInvalidatedUrl { get; init; } = "";
    public DateTimeOffset LastInvalidatedAt { get; init; }
}
