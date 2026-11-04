namespace Yohuke.DanmuPanel.Models.Messages;

/// <summary>保留表情的原文标记和远程图片地址。</summary>
public sealed record MessageEmoticon
{
    public string Text { get; init; } = "";
    public string Url { get; init; } = "";
}
