namespace Yohuke.DanmuPanel.Services;

internal sealed class AvatarDownloadOwnerGate
{
    internal SemaphoreSlim Semaphore { get; } = new(1, 1);
    internal int BorrowerCount { get; set; }
}
