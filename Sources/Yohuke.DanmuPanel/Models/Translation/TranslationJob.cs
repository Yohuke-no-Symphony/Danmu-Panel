using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.Models.Translation;

/// <summary>随消息事务持久保存的翻译任务。</summary>
public sealed record TranslationJob
{
    public string Id { get; init; } = "";
    public string MessageId { get; init; } = "";
    public string RoomTabId { get; init; } = "";
    public MessageKind? Kind { get; init; }
    public long Generation { get; init; }
    public string Text { get; init; } = "";
    public IReadOnlyList<string> EmoticonTexts { get; init; } = [];
    public int Priority { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string ConfigurationFingerprint { get; init; } = "";
    /// <summary>手动重译的唯一标识；非空时跳过缓存，并用于拒绝此前任务的回调。</summary>
    public string RefreshId { get; init; } = "";
}
