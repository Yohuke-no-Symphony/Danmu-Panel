using System.Text.Json.Serialization;
using Yohuke.DanmuPanel.Models.State;

namespace Yohuke.DanmuPanel.Models.Settings;

/// <summary>带产品标记和格式版本的全局设置备份及分享文件。</summary>
public sealed class SettingsSharePackage
{
    public const string FORMAT = "Yohuke.DanmuPanel.Settings";
    public const int CURRENT_VERSION = 1;

    [JsonRequired]
    public string Format { get; set; } = FORMAT;

    [JsonRequired]
    public int Version { get; set; } = CURRENT_VERSION;

    [JsonRequired]
    public PanelSettings Settings { get; set; } = new();
}
