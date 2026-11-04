using Yohuke.DanmuPanel.Models.Updates;

namespace Yohuke.DanmuPanel.Services.Updates;

/// <summary>提供应用最新正式发布，不参与本地版本比较和界面展示。</summary>
public interface IAppUpdateReleaseSource
{
    /// <summary>读取最新正式发布；没有正式发布时返回空，读取失败时抛出异常。</summary>
    Task<AppUpdateInfo?> GetLatestAsync(CancellationToken cancellationToken = default);
}
