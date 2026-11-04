using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Services.Messages;

namespace Yohuke.DanmuPanel.Tests.Integration;

internal sealed class BlockingReceptionStore(IMessageStore inner) : IMessageStore
{
    internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<MessageAppendResult> AppendAsync(MessageRecord message, MessageReadOptions options,
        string? translationFingerprint = null, CancellationToken cancellationToken = default)
    {
        Entered.TrySetResult();
        await Release.Task;
        return await inner.AppendAsync(message, options, translationFingerprint, cancellationToken);
    }

    public Task<IReadOnlyList<MessageRecord>> LoadPageAsync(string roomTabId, MessageKind kind, int offset, int limit,
        CancellationToken cancellationToken = default) => inner.LoadPageAsync(roomTabId, kind, offset, limit, cancellationToken);
    public Task<IReadOnlyList<MessageRecord>> LoadRecentAsync(string roomTabId, MessageKind kind, int limit,
        CancellationToken cancellationToken = default) => inner.LoadRecentAsync(roomTabId, kind, limit, cancellationToken);
    public Task<MessageRecord?> GetByIdAsync(string id, CancellationToken cancellationToken = default) => inner.GetByIdAsync(id, cancellationToken);
    public Task<MessageRecord?> TogglePinAsync(string id, CancellationToken cancellationToken = default) => inner.TogglePinAsync(id, cancellationToken);
    public Task<MessageRecord?> MarkReadAsync(string id, int observedQuantity, decimal observedTotalPrice,
        CancellationToken cancellationToken = default) => inner.MarkReadAsync(id, observedQuantity, observedTotalPrice, cancellationToken);
    public Task<int> MarkAllReadAsync(string roomTabId, MessageKind kind, CancellationToken cancellationToken = default)
        => inner.MarkAllReadAsync(roomTabId, kind, cancellationToken);
    public Task ClearRoomAsync(string roomTabId, CancellationToken cancellationToken = default) => inner.ClearRoomAsync(roomTabId, cancellationToken);
    public Task ClearAllAsync(CancellationToken cancellationToken = default) => inner.ClearAllAsync(cancellationToken);
    public Task<bool> ApplyTranslationAsync(string id, long generation, string fingerprint, string translation,
        CancellationToken cancellationToken = default) => inner.ApplyTranslationAsync(id, generation, fingerprint, translation, cancellationToken);
    public Task<long> GetGenerationAsync(string roomTabId, CancellationToken cancellationToken = default) => inner.GetGenerationAsync(roomTabId, cancellationToken);
    public Task ReevaluateReadOptionsAsync(MessageReadOptions options, CancellationToken cancellationToken = default) => inner.ReevaluateReadOptionsAsync(options, cancellationToken);
    public Task<bool> UpsertTranslationJobAsync(string id, long generation, string fingerprint,
        CancellationToken cancellationToken = default) => inner.UpsertTranslationJobAsync(id, generation, fingerprint, cancellationToken);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
