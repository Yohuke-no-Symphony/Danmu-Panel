namespace Yohuke.DanmuPanel.Models.Messages;

/// <summary>特别关注用户的稳定身份及可选显示名称。</summary>
public sealed class SpecialAttentionUser
{
    public long UserUid { get; set; }
    public string DisplayName { get; set; } = "";
}
