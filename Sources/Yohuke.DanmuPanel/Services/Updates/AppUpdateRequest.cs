namespace Yohuke.DanmuPanel.Services.Updates;

internal sealed class AppUpdateRequest(bool isManual)
{
    internal bool IsManual { get; set; } = isManual;
    internal CancellationTokenSource Cancellation { get; } = new();
    internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
