using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.Appearance;
using Yohuke.DanmuPanel.Models.Translation;

namespace Yohuke.DanmuPanel.Models.State;

/// <summary>阅读台的全局设置。</summary>
public sealed class PanelSettings
{
    public string Language { get; set; } = "system";
    public string Theme { get; set; } = "system";
    public ColorSchemeSettings ColorScheme { get; set; } = new();

    /// <summary>在 macOS 主窗口内显示菜单；默认使用系统菜单栏。</summary>
    public bool UseWindowMenuOnMac { get; set; }

    /// <summary>启动后后台检查 GitHub 正式发布，旧配置默认开启。</summary>
    public bool CheckForUpdatesOnStartup { get; set; } = true;

    public TranslationSettings Translation { get; set; } = new();
    public MessageReadOptions Reading { get; set; } = new();
    public bool ShowUserPinyin { get; set; }
    public bool ShowGiftPinyin { get; set; }

    /// <summary>为四类消息启用 Neo 紧凑显示，旧配置默认启用。</summary>
    public bool CompactDanmuDisplay { get; set; } = true;

    /// <summary>紧凑显示时弹幕、礼物及上舰正文是否自动换行。</summary>
    public bool CompactDanmuWrap { get; set; }

    /// <summary>紧凑显示时 SC 正文是否自动换行。</summary>
    public bool CompactSuperChatWrap { get; set; } = true;

    /// <summary>自动循环滚动超出单行宽度的消息正文与译文，默认开启。</summary>
    public bool AutoScrollLongMessages { get; set; } = true;

    public string? DefaultAccountId { get; set; }
    public string? SelectedWorkspaceId { get; set; }
}
