using Yohuke.DanmuPanel.Models.Updates;

namespace Yohuke.DanmuPanel.Services.Updates;

/// <summary>从可替换的发布源读取正式版本，并按数字比较规范化后的版本号。</summary>
/// <param name="source">应用的正式发布源。</param>
public sealed class AppUpdateService(IAppUpdateReleaseSource source) : IAppUpdateService
{
    /// <inheritdoc/>
    public async Task<AppUpdateCheckResult> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var release = await Task.Run(() => source.GetLatestAsync(cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (release is null)
        {
            return new(AppUpdateCheckStatus.NO_RELEASE, null);
        }

        var status = AppUpdateVersion.Normalize(release.Version) > AppUpdateVersion.Normalize(currentVersion)
            ? AppUpdateCheckStatus.UPDATE_AVAILABLE : AppUpdateCheckStatus.UP_TO_DATE;
        return new(status, release);
    }
}
