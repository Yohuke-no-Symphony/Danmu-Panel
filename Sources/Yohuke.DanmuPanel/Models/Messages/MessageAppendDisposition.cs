namespace Yohuke.DanmuPanel.Models.Messages;

/// <summary>消息持久化的处理结果。</summary>
public enum MessageAppendDisposition
{
    Appended,
    Updated,
    Duplicate,
    Stale,
    ClearedHistory
}
