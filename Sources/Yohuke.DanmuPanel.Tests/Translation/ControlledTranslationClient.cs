using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services.Translation;

namespace Yohuke.DanmuPanel.Tests.Translation;

internal sealed class ControlledTranslationClient : ITranslationClient
{
    internal TaskCompletionSource<string> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int Calls;
    internal bool WasCancelled;

    public async Task<string> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Calls);
        Started.TrySetResult();
        try
        {
            return await Response.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            WasCancelled = true;
            throw;
        }
    }
}
