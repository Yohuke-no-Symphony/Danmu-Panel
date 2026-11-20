using Yohuke.DanmuPanel.Models.Translation;

namespace Yohuke.DanmuPanel.Services.Translation;

internal sealed class SharedTranslationRequest(TranslationRequest request)
{
    internal TranslationRequest Request { get; } = request;
    internal CancellationTokenSource Cancellation { get; } = new();
    internal TaskCompletionSource<string> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task? CancellationTask { get; set; }
    internal int Waiters { get; set; }
    internal bool Finished { get; set; }
}
