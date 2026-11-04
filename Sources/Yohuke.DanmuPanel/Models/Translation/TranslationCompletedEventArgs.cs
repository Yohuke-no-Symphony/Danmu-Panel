namespace Yohuke.DanmuPanel.Models.Translation;

/// <summary>译文已通过消息代次校验并完成持久化。</summary>
public sealed class TranslationCompletedEventArgs(TranslationJob job, string translation) : EventArgs
{
    public string MessageId { get; } = job.MessageId;
    public string RoomTabId { get; } = job.RoomTabId;
    public long Generation { get; } = job.Generation;
    public string Translation { get; } = translation;
}
