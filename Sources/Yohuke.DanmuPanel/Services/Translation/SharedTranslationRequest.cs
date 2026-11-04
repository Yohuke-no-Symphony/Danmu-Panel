namespace Yohuke.DanmuPanel.Services.Translation;

internal sealed class SharedTranslationRequest(CancellationToken lifetime)
{
    internal CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    internal Task<string>? Task { get; set; }
    internal int Waiters { get; set; }
    internal bool Finished { get; set; }
    internal bool Cancelling { get; set; }
}
