namespace Yohuke.DanmuPanel.Models.Messages;

/// <summary>可添加至特别关注的 B 站用户公开身份。</summary>
public sealed class UserSearchResult
{
    public long UserUid { get; init; }
    public string DisplayName { get; init; } = "";
    public string AvatarUrl { get; init; } = "";
}
