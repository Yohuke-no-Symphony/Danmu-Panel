using Yohuke.DanmuPanel.Models.Updates;

namespace Yohuke.DanmuPanel.Services.Updates;

/// <summary>比较最新正式发布与应用当前版本。</summary>
public interface IAppUpdateService
{
    /// <summary>完成一次可取消的版本检查，保留无发布和无更新的不同结果。</summary>
    Task<AppUpdateCheckResult> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default);
}
