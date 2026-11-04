using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services.Translation;

namespace Yohuke.DanmuPanel.ViewModels;

internal sealed class MessageDisplaySettings
{
    private readonly Dictionary<string, string> colors;
    private readonly Dictionary<string, IBrush> brushes = new(StringComparer.Ordinal);

    internal string TranslationFingerprint { get; }
    internal TranslationDisplayMode TranslationDisplayMode { get; }
    internal bool ShowUserPinyin { get; }
    internal bool ShowGiftPinyin { get; }
    internal bool ShowDanmuPinyin { get; }
    internal bool ShowSuperChatPinyin { get; }
    internal PinyinLayoutMode PinyinLayout { get; }
    internal bool AutoScrollLongMessages { get; }
    internal string LanguageId { get; }
    internal string CultureName { get; }

    private MessageDisplaySettings(PanelSettings settings, string fingerprint, string languageId)
    {
        TranslationFingerprint = fingerprint;
        TranslationDisplayMode = settings.Translation.DisplayMode;
        ShowUserPinyin = settings.ShowUserPinyin;
        ShowGiftPinyin = settings.ShowGiftPinyin;
        ShowDanmuPinyin = settings.ShowDanmuPinyin;
        ShowSuperChatPinyin = settings.ShowSuperChatPinyin;
        PinyinLayout = settings.PinyinLayout;
        AutoScrollLongMessages = settings.AutoScrollLongMessages;
        LanguageId = languageId;
        CultureName = CultureInfo.CurrentCulture.Name;
        colors = new(settings.ColorScheme.Overrides, StringComparer.Ordinal);
    }

    internal static MessageDisplaySettings Capture(PanelSettings settings, MessageDisplaySettings? previous = null)
    {
        var fingerprint = TranslationRequestFactory.GetConfigurationFingerprint(settings.Translation);
        var languageId = Localization.IsInitialized ? Localization.CurrentLanguageID : "";
        if (previous is not null && previous.TranslationFingerprint == fingerprint &&
            previous.TranslationDisplayMode == settings.Translation.DisplayMode &&
            previous.ShowUserPinyin == settings.ShowUserPinyin && previous.ShowGiftPinyin == settings.ShowGiftPinyin &&
            previous.ShowDanmuPinyin == settings.ShowDanmuPinyin &&
            previous.ShowSuperChatPinyin == settings.ShowSuperChatPinyin &&
            previous.PinyinLayout == settings.PinyinLayout &&
            previous.AutoScrollLongMessages == settings.AutoScrollLongMessages && previous.LanguageId == languageId &&
            previous.CultureName == CultureInfo.CurrentCulture.Name && previous.HasSameColors(settings))
        {
            return previous;
        }
        return new(settings, fingerprint, languageId);
    }

    internal string GetColorOverride(string key, string fallback)
    {
        return colors.GetValueOrDefault(key, fallback);
    }

    internal IBrush GetColor(string key, string fallback)
    {
        var color = GetColorOverride(key, fallback);
        if (!brushes.TryGetValue(color, out var brush))
        {
            brush = new ImmutableSolidColorBrush(Color.Parse(color));
            brushes.Add(color, brush);
        }
        return brush;
    }

    private bool HasSameColors(PanelSettings settings)
    {
        var overrides = settings.ColorScheme.Overrides;
        if (colors.Count != overrides.Count)
        {
            return false;
        }
        foreach (var (key, value) in colors)
        {
            if (!overrides.TryGetValue(key, out var current) || current != value)
            {
                return false;
            }
        }
        return true;
    }
}
