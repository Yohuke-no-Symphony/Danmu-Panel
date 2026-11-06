namespace Yohuke.DanmuPanel.Services;

/// <summary>从用户资料查询当前头像地址。</summary>
public interface IUserAvatarSource
{
    /// <summary>返回指定用户的头像地址；没有可用资料时返回空字符串。</summary>
    Task<string> GetAvatarUrlAsync(long userUid, CancellationToken cancellationToken = default);
}
