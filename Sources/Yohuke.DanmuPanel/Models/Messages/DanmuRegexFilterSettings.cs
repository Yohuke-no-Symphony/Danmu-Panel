using System.Text.Json.Serialization;

namespace Yohuke.DanmuPanel.Models.Messages;

/// <summary>普通弹幕的逐条屏蔽规则，默认关闭并预填同传和音乐规则。</summary>
[JsonConverter(typeof(DanmuRegexFilterSettingsJsonConverter))]
public sealed class DanmuRegexFilterSettings
{
    public List<DanmuRegexRuleSettings> Rules { get; set; } =
    [
        new() { Pattern = @"^(.*?)【(.*\*?)】$" },
        new() { Pattern = @"(?:[♩♪♫♬♭♮♯]|🎵|🎶)" }
    ];
}
