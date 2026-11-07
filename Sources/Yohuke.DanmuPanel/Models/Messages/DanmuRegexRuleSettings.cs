namespace Yohuke.DanmuPanel.Models.Messages;

/// <summary>一条普通弹幕正则及其独立显示、翻译屏蔽偏好。</summary>
public sealed class DanmuRegexRuleSettings
{
    public string Pattern { get; set; } = "";
    public bool BlockDisplay { get; set; }
    public bool BlockTranslation { get; set; }
}
