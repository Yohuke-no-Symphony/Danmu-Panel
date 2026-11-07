namespace Yohuke.DanmuPanel.Models.Messages;

/// <summary>跨直播间共用的特别关注名单，仅按用户 UID 匹配。</summary>
public sealed class SpecialAttentionSettings
{
    public List<SpecialAttentionUser> Users { get; set; } = [];
}
