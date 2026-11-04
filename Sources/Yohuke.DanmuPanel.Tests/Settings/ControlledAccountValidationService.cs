using Yohuke.DanmuPanel.Models.Workspace;
using Yohuke.DanmuPanel.Services;

namespace Yohuke.DanmuPanel.Tests.Settings;

internal sealed class ControlledAccountValidationService : IBilibiliAccountValidationService
{
    private readonly TaskCompletionSource<AccountValidationResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<AccountValidationResult> ValidateAsync(string cookie, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        return completion.Task;
    }

    internal void Complete(AccountValidationResult result)
    {
        completion.TrySetResult(result);
    }
}
