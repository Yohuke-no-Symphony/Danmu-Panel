namespace Yohuke.DanmuPanel.Models.Messages;

/// <summary>普通弹幕的全局正则屏蔽偏好，缺失配置时默认屏蔽同传格式。</summary>
public sealed class DanmuRegexFilterSettings
{
    public bool Enabled { get; set; } = true;
    public List<string> Patterns { get; set; } = [@"^(.*?)【(.*\*?)】$"];
}
