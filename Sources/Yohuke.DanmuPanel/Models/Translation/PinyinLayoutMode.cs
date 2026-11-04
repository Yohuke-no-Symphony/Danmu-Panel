namespace Yohuke.DanmuPanel.Models.Translation;

/// <summary>用户名、礼物名、弹幕及 SC 共用的拼音排版方式。</summary>
public enum PinyinLayoutMode
{
    /// <summary>在每个汉字上方对齐对应音节。</summary>
    Ruby,

    /// <summary>在原文外以独立一行显示拼音。</summary>
    SeparateLine
}
