using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.Appearance;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services.Messages;
using Yohuke.DanmuPanel.Services.Translation;

namespace Yohuke.DanmuPanel.ViewModels;

internal sealed class MessageDisplaySettings
{
    private readonly Dictionary<string, string> colors;
    private readonly Dictionary<string, IBrush> brushes = new(StringComparer.Ordinal);
    private readonly HashSet<long> specialAttentionUids;
    private readonly MessageBorderSettings messageBorders;

    internal string TranslationFingerprint { get; }
    internal bool TranslationEnabled { get; }
    internal TranslationDisplayMode TranslationDisplayMode { get; }
    internal DanmuRegexFilter DanmuFilter { get; }
    internal bool ShowUserPinyin { get; }
    internal bool ShowUserAvatars { get; }
    internal bool ShowFanMedals { get; }
    internal bool ShowGiftPinyin { get; }
    internal bool ShowDanmuPinyin { get; }
    internal bool ShowSuperChatPinyin { get; }
    internal PinyinLayoutMode PinyinLayout { get; }
    internal bool AutoScrollLongMessages { get; }
    internal string LanguageId { get; }
    internal string CultureName { get; }
    internal MessagePresentationOptions PresentationOptions { get; }

    private MessageDisplaySettings(PanelSettings settings, string fingerprint, string languageId, DanmuRegexFilter danmuFilter)
    {
        TranslationFingerprint = fingerprint;
        TranslationEnabled = settings.Translation.Enabled;
        TranslationDisplayMode = settings.Translation.DisplayMode;
        DanmuFilter = danmuFilter;
        ShowUserPinyin = settings.ShowUserPinyin;
        ShowUserAvatars = settings.ShowUserAvatars;
        ShowFanMedals = settings.ShowFanMedals;
        ShowGiftPinyin = settings.ShowGiftPinyin;
        ShowDanmuPinyin = settings.ShowDanmuPinyin;
        ShowSuperChatPinyin = settings.ShowSuperChatPinyin;
        PinyinLayout = settings.PinyinLayout;
        AutoScrollLongMessages = settings.AutoScrollLongMessages;
        LanguageId = languageId;
        CultureName = CultureInfo.CurrentCulture.Name;
        PresentationOptions = new(ShowUserPinyin, ShowGiftPinyin, ShowDanmuPinyin, ShowSuperChatPinyin, CultureName);
        colors = new(settings.ColorScheme.Overrides, StringComparer.Ordinal);
        specialAttentionUids = settings.SpecialAttention.Users.Select(user => user.UserUid).ToHashSet();
        messageBorders = new()
        {
            SuperChat = settings.MessageBorders.SuperChat,
            Gift = settings.MessageBorders.Gift,
            Guard = settings.MessageBorders.Guard
        };
    }

    internal static MessageDisplaySettings Capture(PanelSettings settings, MessageDisplaySettings? previous = null,
        DanmuRegexFilter? danmuFilter = null)
    {
        var fingerprint = TranslationRequestFactory.GetConfigurationFingerprint(settings.Translation);
        var languageId = Localization.IsInitialized ? Localization.CurrentLanguageID : "";
        danmuFilter ??= previous?.DanmuFilter.HasSameSettings(settings.DanmuRegexFilter) == true
            ? previous.DanmuFilter : new(settings.DanmuRegexFilter);
        if (previous is not null && previous.TranslationFingerprint == fingerprint &&
            ReferenceEquals(previous.DanmuFilter, danmuFilter) &&
            previous.TranslationEnabled == settings.Translation.Enabled &&
            previous.TranslationDisplayMode == settings.Translation.DisplayMode &&
            previous.ShowUserPinyin == settings.ShowUserPinyin && previous.ShowGiftPinyin == settings.ShowGiftPinyin &&
            previous.ShowUserAvatars == settings.ShowUserAvatars &&
            previous.ShowFanMedals == settings.ShowFanMedals &&
            previous.messageBorders.SuperChat == settings.MessageBorders.SuperChat &&
            previous.messageBorders.Gift == settings.MessageBorders.Gift &&
            previous.messageBorders.Guard == settings.MessageBorders.Guard &&
            previous.ShowDanmuPinyin == settings.ShowDanmuPinyin &&
            previous.ShowSuperChatPinyin == settings.ShowSuperChatPinyin &&
            previous.PinyinLayout == settings.PinyinLayout &&
            previous.AutoScrollLongMessages == settings.AutoScrollLongMessages && previous.LanguageId == languageId &&
            previous.CultureName == CultureInfo.CurrentCulture.Name && previous.HasSameColors(settings) &&
            previous.HasSameSpecialAttention(settings))
        {
            return previous;
        }
        return new(settings, fingerprint, languageId, danmuFilter);
    }

    internal bool IsTranslationBlocked(MessageRecord record)
    {
        return record.Kind == MessageKind.Danmu && DanmuFilter.IsTranslationBlocked(record.Content);
    }

    internal double GetBorderThickness(MessageKind kind)
    {
        return messageBorders.GetThickness(kind);
    }

    internal bool IsSpecialAttention(long userUid)
    {
        return userUid > 0 && specialAttentionUids.Contains(userUid);
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

    private bool HasSameSpecialAttention(PanelSettings settings)
    {
        var users = settings.SpecialAttention.Users;
        if (specialAttentionUids.Count != users.Count)
        {
            return false;
        }
        foreach (var user in users)
        {
            if (!specialAttentionUids.Contains(user.UserUid))
            {
                return false;
            }
        }
        return true;
    }
}
