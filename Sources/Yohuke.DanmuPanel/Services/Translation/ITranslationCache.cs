namespace Yohuke.DanmuPanel.Services.Translation;

/// <summary>与消息永久译文分离的可淘汰请求缓存。</summary>
public interface ITranslationCache
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);
    Task SetAsync(string key, string translation, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}
