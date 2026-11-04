namespace Yohuke.DanmuPanel.Models.Translation;

/// <summary>随消息事务持久保存的翻译任务。</summary>
public sealed record TranslationJob
{
    public string Id { get; init; } = "";
    public string MessageId { get; init; } = "";
    public string RoomTabId { get; init; } = "";
    public long Generation { get; init; }
    public string Text { get; init; } = "";
    public IReadOnlyList<string> EmoticonTexts { get; init; } = [];
    public int Priority { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string ConfigurationFingerprint { get; init; } = "";
}
