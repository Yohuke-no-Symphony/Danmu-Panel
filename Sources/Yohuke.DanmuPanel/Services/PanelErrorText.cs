using Yohuke.DanmuPanel.I18n;

namespace Yohuke.DanmuPanel.Services;

/// <summary>将稳定错误码转换为当前界面语言。</summary>
public static class PanelErrorText
{
    /// <summary>本地化应用错误，远端文本保留脱敏后的原文。</summary>
    public static string Render(string text)
    {
        var separator = text.IndexOf(':');
        if (text.StartsWith("translation.", StringComparison.Ordinal) && separator > 0)
        {
            return Localization.Format(text[..separator], text[(separator + 1)..]);
        }
        return Localization.Get(text);
    }
}
