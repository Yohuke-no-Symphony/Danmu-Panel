using Yohuke.DanmuPanel.Models.Updates;
using Yohuke.DanmuPanel.Services.Updates;

namespace Yohuke.DanmuPanel.Tests.Updates;

internal sealed class AppUpdateReleaseSourceStub(
    Func<CancellationToken, Task<AppUpdateInfo?>> getLatest) : IAppUpdateReleaseSource
{
    /// <inheritdoc/>
    public Task<AppUpdateInfo?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        return getLatest(cancellationToken);
    }
}
