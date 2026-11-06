namespace Yohuke.DanmuPanel.Models.Messages;

/// <summary>普通弹幕的全局正则屏蔽偏好，默认关闭并保留同传格式规则。</summary>
public sealed class DanmuRegexFilterSettings
{
    public bool Enabled { get; set; }
    public List<string> Patterns { get; set; } = [@"^(.*?)【(.*\*?)】$"];
}
