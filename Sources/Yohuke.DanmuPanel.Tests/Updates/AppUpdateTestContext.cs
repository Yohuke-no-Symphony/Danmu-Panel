using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Models.Updates;
using Yohuke.DanmuPanel.ViewModels.Updates;

namespace Yohuke.DanmuPanel.Tests.Updates;

internal sealed class AppUpdateTestContext : IAsyncDisposable
{
    internal ControlledAppUpdateService Service { get; } = new();
    internal AppUpdateViewModel Model { get; }
    internal Exception? Failure { get; private set; }
    internal static AppUpdateInfo Update => new(new(2, 0, 0), "v2.0.0", "版本 2", "修复与改进",
        new("https://github.com/Yohuke-no-Symphony/DanmuPanel/releases/tag/v2.0.0"));

    internal AppUpdateTestContext(PanelSettings? settings = null, Func<Uri, Task<bool>>? launchUri = null)
    {
        Model = new(Service, settings ?? new(), new(1, 0, 0),
            launchUri ?? (_ => Task.FromResult(true)), error => Failure = error);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        foreach (var response in Service.Requests)
        {
            response.TrySetResult(new(AppUpdateCheckStatus.NO_RELEASE, null));
        }
        await Model.DisposeAsync();
    }
}
