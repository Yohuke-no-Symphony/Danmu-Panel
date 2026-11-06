namespace Yohuke.DanmuPanel.Services;

/// <summary>提供按用户共享的头像缓存和后台更新通知。</summary>
public interface IUserAvatarService
{
    /// <summary>指定用户的头像文件发生变化。</summary>
    event Action<long>? AvatarChanged;

    /// <summary>读取用户头像；过期缓存立即返回并在后台更新，缺少缓存时等待首次获取。</summary>
    Task<string> GetAvatarAsync(long userUid, CancellationToken cancellationToken = default);

    /// <summary>使无法读取的当前头像失效；过期请求不能删除同一用户的更新版本。</summary>
    Task InvalidateAvatarAsync(long userUid, string invalidPath, CancellationToken cancellationToken = default);
}
