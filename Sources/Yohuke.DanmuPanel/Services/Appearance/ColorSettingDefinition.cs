namespace Yohuke.DanmuPanel.Services.Appearance;

/// <summary>一个可编辑颜色的资源标识、界面名称及分组。</summary>
public sealed record ColorSettingDefinition(string Key, string NameKey, string GroupKey)
{
    internal string? DefaultResourceKey { get; init; }
    internal string? DefaultColor { get; init; }
}
