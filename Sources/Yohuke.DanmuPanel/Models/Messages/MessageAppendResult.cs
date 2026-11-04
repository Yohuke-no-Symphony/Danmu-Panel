namespace Yohuke.DanmuPanel.Models.Messages;

/// <summary>仅在提交成功并产生可见变化时包含消息。</summary>
public sealed record MessageAppendResult(MessageAppendDisposition Disposition, MessageRecord? Record = null);
