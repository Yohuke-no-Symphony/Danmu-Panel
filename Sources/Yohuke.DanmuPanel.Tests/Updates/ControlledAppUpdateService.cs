using Yohuke.DanmuPanel.Models.Updates;
using Yohuke.DanmuPanel.Services.Updates;

namespace Yohuke.DanmuPanel.Tests.Updates;

internal sealed class ControlledAppUpdateService : IAppUpdateService
{
    internal TaskCompletionSource<AppUpdateCheckResult> Response { get; set; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal List<TaskCompletionSource<AppUpdateCheckResult>> Requests { get; } = [];
    internal Version? CurrentVersion { get; private set; }
    internal CancellationToken CancellationToken { get; private set; }
    internal int CallCount => Requests.Count;

    /// <inheritdoc/>
    public Task<AppUpdateCheckResult> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default)
    {
        CurrentVersion = currentVersion;
        CancellationToken = cancellationToken;
        Requests.Add(Response);
        return Response.Task;
    }
}
