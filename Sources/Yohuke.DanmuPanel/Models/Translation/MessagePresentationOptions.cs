namespace Yohuke.DanmuPanel.Models.Translation;

internal sealed record MessagePresentationOptions(
    bool ShowUserPinyin,
    bool ShowGiftPinyin,
    bool ShowDanmuPinyin,
    bool ShowSuperChatPinyin,
    string CultureName);
