namespace Yohuke.DanmuPanel.Models.Updates;

/// <summary>更新检查的结果，保留发布信息以供界面展示。</summary>
/// <param name="Status">发布与当前版本的关系。</param>
/// <param name="Release">最新正式发布；仓库没有正式发布时为空。</param>
public sealed record AppUpdateCheckResult(AppUpdateCheckStatus Status, AppUpdateInfo? Release);
