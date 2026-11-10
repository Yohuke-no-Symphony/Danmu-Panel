namespace Yohuke.DanmuPanel.Models.Messages;

/// <summary>供内联徽章显示的被回复用户身份。</summary>
public sealed record MessageReply(long UserUid, string UserName, string UserNameColor);
