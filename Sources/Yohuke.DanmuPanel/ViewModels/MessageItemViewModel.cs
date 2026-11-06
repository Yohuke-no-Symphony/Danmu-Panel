using System.Globalization;
using Avalonia.Media;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Models.Workspace;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.Services.Appearance;

namespace Yohuke.DanmuPanel.ViewModels;

/// <summary>消息的显示投影，保留原始名称与阅读状态。</summary>
public sealed class MessageItemViewModel : FeedRowViewModel
{
    private const int MAX_MEDAL_NAME_TEXT_ELEMENTS = 3;
    private readonly IPinyinService pinyin;
    private readonly bool deferPresentation;
    private MessagePresentationSnapshot? presentation;
    private bool isPresentationPending;
    private MessageDisplaySettings settings;
    private string namePinyin = "";
    private IReadOnlyList<PinyinAnnotation> nameAnnotations = [];
    private string detailPinyin = "";
    private IReadOnlyList<PinyinAnnotation> contentPinyin = [];
    private string medal = "";
    private string medalName = "";
    private string time = "";
    private string? detail;
    private string price = "";
    private string translation = "";
    private bool translationPending;
    private bool hasTranslatableContent;
    private IBrush borderBrush = Brushes.Transparent;
    private IBrush medalBorderBrush = Brushes.Transparent;
    private IReadOnlyList<DanmuEmoticon> emoticons = [];
    public MessageRecord Record { get; private set; }
    internal bool IsPresentationPending => isPresentationPending;
    internal MessageDisplaySettings PresentationSettings => settings;
    internal bool TranslationPending => translationPending;
    public string Name => Record.UserName;
    /// <summary>当前消息用户的稳定身份，用于独立于消息行缓存头像。</summary>
    public long UserUid => Record.UserUid;
    /// <summary>是否显示消息用户头像。</summary>
    public bool ShowUserAvatars => settings.ShowUserAvatars;
    public string NamePinyin => namePinyin;
    public bool HasNamePinyin => NamePinyin.Length > 0;
    public bool IsNamePinyinPending => isPresentationPending && settings.ShowUserPinyin &&
        ContainsChinese(Record.UserName) && !Record.UserName.Any(character =>
            character is >= '\u3040' and <= '\u30FF' or >= '\uFF66' and <= '\uFF9F');
    public IReadOnlyList<PinyinAnnotation> NameAnnotations => nameAnnotations;
    public string Medal => medal;
    public string MedalName => medalName;
    public int MedalLevel => Record.MedalLevel;
    public IBrush MedalBorderBrush => medalBorderBrush;
    public string Time => time;
    public bool ShowPinBadge => Record.Kind == MessageKind.Danmu && Record.IsPinned;
    public bool ShowUnreadDot => Record.Kind is MessageKind.SuperChat or MessageKind.Gift or MessageKind.Guard && !Record.IsRead;
    public string Detail => detail ??= FormatDetail();
    public string DetailPinyin => detailPinyin;
    public bool HasDetailPinyin => DetailPinyin.Length > 0;
    public IReadOnlyList<PinyinAnnotation> ContentPinyin => contentPinyin;
    public bool HasContentPinyin => contentPinyin.Count > 0;
    public bool IsContentPinyinPending => isPresentationPending && IsContentPinyinEnabled(Record.Kind, settings) &&
        ContainsChinese(Record.Kind == MessageKind.Gift ? Record.GiftName : Record.Content);
    public PinyinLayoutMode PinyinLayout => settings.PinyinLayout;
    public string Price => price;
    public string Translation => translation;
    public bool HasTranslation => Translation.Length > 0 && !string.Equals(Translation, Record.Content, StringComparison.Ordinal);
    public bool IsTranslating => settings.TranslationEnabled && translationPending && hasTranslatableContent && Translation.Length == 0;
    public bool ShowTranslation => HasTranslation || IsTranslating;
    public bool ShowOriginal => !HasTranslation || settings.TranslationDisplayMode == TranslationDisplayMode.Bilingual;
    public bool HasMedal => Medal.Length > 0;
    public bool AutoScrollLongMessages => settings.AutoScrollLongMessages;
    public IBrush BorderBrush => borderBrush;
    public IReadOnlyList<DanmuEmoticon> Emoticons
    {
        get => emoticons;
        set => SetProperty(ref emoticons, value);
    }

    /// <summary>创建单行显示投影。</summary>
    public MessageItemViewModel(MessageRecord record, PanelSettings settings, IPinyinService pinyin)
        : this(record, MessageDisplaySettings.Capture(settings), pinyin)
    {
    }

    private MessageItemViewModel(MessageRecord record, MessageDisplaySettings settings, IPinyinService pinyin,
        bool isTranslationPending = false, bool deferPresentation = false)
    {
        Record = record;
        this.settings = settings;
        this.pinyin = pinyin;
        this.deferPresentation = deferPresentation;
        Update(record, settings, true, isTranslationPending);
    }

    /// <summary>原位更新，保留列表容器及阅读位置。</summary>
    public void Update(MessageRecord record, PanelSettings currentSettings)
    {
        Apply(record, MessageDisplaySettings.Capture(currentSettings, settings));
    }

    internal static MessageItemViewModel Create(MessageRecord record, MessageDisplaySettings settings, IPinyinService pinyin,
        bool isTranslationPending = false)
    {
        return new(record, settings, pinyin, isTranslationPending);
    }

    internal static MessageItemViewModel CreateDeferred(MessageRecord record, MessageDisplaySettings settings, IPinyinService pinyin,
        bool isTranslationPending)
    {
        return new(record, settings, pinyin, isTranslationPending, deferPresentation: true);
    }

    internal bool ApplyPresentation(MessagePresentationSnapshot value)
    {
        if (!value.Matches(Record, settings.PresentationOptions))
        {
            return false;
        }
        presentation = value;
        var wasTranslating = IsTranslating;
        var showedTranslation = ShowTranslation;
        var hadName = HasNamePinyin;
        var hadDetail = HasDetailPinyin;
        var hadContent = HasContentPinyin;
        var nameWasPending = IsNamePinyinPending;
        var contentWasPending = IsContentPinyinPending;
        SetProperty(ref namePinyin, value.NamePinyin, nameof(NamePinyin));
        SetProperty(ref nameAnnotations, value.NameAnnotations, nameof(NameAnnotations));
        SetProperty(ref detailPinyin, value.DetailPinyin, nameof(DetailPinyin));
        SetProperty(ref contentPinyin, value.ContentPinyin, nameof(ContentPinyin));
        hasTranslatableContent = value.HasTranslatableContent;
        isPresentationPending = false;
        NotifyChange(nameWasPending, IsNamePinyinPending, nameof(IsNamePinyinPending));
        NotifyChange(contentWasPending, IsContentPinyinPending, nameof(IsContentPinyinPending));
        NotifyChange(hadName, HasNamePinyin, nameof(HasNamePinyin));
        NotifyChange(hadDetail, HasDetailPinyin, nameof(HasDetailPinyin));
        NotifyChange(hadContent, HasContentPinyin, nameof(HasContentPinyin));
        NotifyChange(wasTranslating, IsTranslating, nameof(IsTranslating));
        NotifyChange(showedTranslation, ShowTranslation, nameof(ShowTranslation));
        return true;
    }

    internal void SetTranslationPending(bool value)
    {
        var wasTranslating = IsTranslating;
        var showedTranslation = ShowTranslation;
        translationPending = value;
        NotifyChange(wasTranslating, IsTranslating, nameof(IsTranslating));
        NotifyChange(showedTranslation, ShowTranslation, nameof(ShowTranslation));
    }

    internal void FailPresentation()
    {
        var nameWasPending = IsNamePinyinPending;
        var contentWasPending = IsContentPinyinPending;
        isPresentationPending = false;
        NotifyChange(nameWasPending, IsNamePinyinPending, nameof(IsNamePinyinPending));
        NotifyChange(contentWasPending, IsContentPinyinPending, nameof(IsContentPinyinPending));
    }

    internal void Apply(MessageRecord record, MessageDisplaySettings currentSettings, bool isTranslationPending = false)
    {
        Update(record, currentSettings, false, isTranslationPending);
    }

    private void Update(MessageRecord record, MessageDisplaySettings currentSettings, bool initialize, bool isTranslationPending)
    {
        if (!initialize && ReferenceEquals(Record, record) && ReferenceEquals(settings, currentSettings) &&
            translationPending == isTranslationPending)
        {
            return;
        }
        var previous = Record;
        var previousSettings = settings;
        var hadNamePinyin = HasNamePinyin;
        var hadDetailPinyin = HasDetailPinyin;
        var hadContentPinyin = HasContentPinyin;
        var nameWasPending = IsNamePinyinPending;
        var contentWasPending = IsContentPinyinPending;
        var hadMedal = HasMedal;
        var hadTranslation = HasTranslation;
        var wasTranslating = IsTranslating;
        var showedTranslation = ShowTranslation;
        var showedOriginal = ShowOriginal;
        var showedPin = ShowPinBadge;
        var showedUnread = ShowUnreadDot;
        Record = record;
        settings = currentSettings;
        translationPending = isTranslationPending;

        if (!deferPresentation && (initialize || previous.Kind != record.Kind || previous.Content != record.Content ||
            !previous.Emoticons.SequenceEqual(record.Emoticons)))
        {
            hasTranslatableContent = record.Kind is MessageKind.Danmu or MessageKind.SuperChat &&
                TranslationTextExtractor.Extract(record.Content, record.Emoticons.Select(emoticon => emoticon.Text)).Length > 0;
        }

        NotifyChange(previous.UserName, record.UserName, nameof(Name));
        NotifyChange(previous.UserUid, record.UserUid, nameof(UserUid));
        NotifyChange(previousSettings.ShowUserAvatars, settings.ShowUserAvatars, nameof(ShowUserAvatars));
        NotifyChange(previous.MedalLevel, record.MedalLevel, nameof(MedalLevel));
        NotifyChange(showedPin, ShowPinBadge, nameof(ShowPinBadge));
        NotifyChange(showedUnread, ShowUnreadDot, nameof(ShowUnreadDot));
        NotifyChange(previousSettings.AutoScrollLongMessages, settings.AutoScrollLongMessages, nameof(AutoScrollLongMessages));
        NotifyChange(previousSettings.PinyinLayout, settings.PinyinLayout, nameof(PinyinLayout));

        if (!deferPresentation && (initialize || previous.UserName != record.UserName || previousSettings.ShowUserPinyin != settings.ShowUserPinyin))
        {
            SetProperty(ref namePinyin, GetAnnotation(Name, settings.ShowUserPinyin), nameof(NamePinyin));
            SetProperty(ref nameAnnotations, HasNamePinyin ? pinyin.GetAnnotations(Name) : [], nameof(NameAnnotations));
        }
        if (!deferPresentation && (initialize || previous.Kind != record.Kind || previous.GiftName != record.GiftName ||
            previousSettings.ShowGiftPinyin != settings.ShowGiftPinyin))
        {
            SetProperty(ref detailPinyin, record.Kind == MessageKind.Gift
                ? GetAnnotation(record.GiftName, settings.ShowGiftPinyin) : "", nameof(DetailPinyin));
        }
        if (!deferPresentation && (initialize || previous.Kind != record.Kind || previous.Content != record.Content ||
            record.Kind == MessageKind.Gift && (previous.GiftName != record.GiftName ||
                previous.Quantity != record.Quantity || previous.TotalPrice != record.TotalPrice ||
                previousSettings.CultureName != settings.CultureName) ||
            IsContentPinyinEnabled(previous.Kind, previousSettings) != IsContentPinyinEnabled(record.Kind, settings)))
        {
            SetProperty(ref contentPinyin, IsContentPinyinEnabled(record.Kind, settings)
                ? GetContentAnnotations(record) : [], nameof(ContentPinyin));
        }
        if (deferPresentation && presentation?.Matches(record, settings.PresentationOptions) != true)
        {
            SetProperty(ref namePinyin, "", nameof(NamePinyin));
            SetProperty(ref nameAnnotations, Array.Empty<PinyinAnnotation>(), nameof(NameAnnotations));
            SetProperty(ref detailPinyin, "", nameof(DetailPinyin));
            SetProperty(ref contentPinyin, Array.Empty<PinyinAnnotation>(), nameof(ContentPinyin));
            hasTranslatableContent = false;
            isPresentationPending = true;
        }
        if (initialize || previous.MedalName != record.MedalName || previous.MedalLevel != record.MedalLevel)
        {
            SetProperty(ref medal, record.MedalLevel > 0 ? $"{record.MedalName} {record.MedalLevel}" : "", nameof(Medal));
            var name = new StringInfo(record.MedalName);
            SetProperty(ref medalName, name.LengthInTextElements <= MAX_MEDAL_NAME_TEXT_ELEMENTS
                ? record.MedalName : name.SubstringByTextElements(0, MAX_MEDAL_NAME_TEXT_ELEMENTS), nameof(MedalName));
        }
        var cultureChanged = previousSettings.CultureName != settings.CultureName;
        if (initialize || previous.Time != record.Time || cultureChanged)
        {
            SetProperty(ref time, record.Time.ToLocalTime().ToString("HH:mm:ss"), nameof(Time));
        }
        if (initialize || previous.Kind != record.Kind || previous.Content != record.Content ||
            previous.GiftName != record.GiftName || previous.Quantity != record.Quantity ||
            previous.TotalPrice != record.TotalPrice || previous.GuardLevel != record.GuardLevel ||
            previousSettings.LanguageId != settings.LanguageId || cultureChanged)
        {
            SetProperty(ref detail, record.Kind != MessageKind.Guard || Localization.IsInitialized
                ? FormatDetail() : null, nameof(Detail));
        }
        if (initialize || previous.Kind != record.Kind || previous.TotalPrice != record.TotalPrice || cultureChanged)
        {
            SetProperty(ref price, record.Kind == MessageKind.SuperChat ? $"￥{record.TotalPrice:0.##}" : "", nameof(Price));
        }
        SetProperty(ref translation, record.TranslationFingerprint == settings.TranslationFingerprint
            ? record.Translation ?? "" : "", nameof(Translation));
        SetProperty(ref borderBrush, GetBorderBrush(), nameof(BorderBrush));
        SetProperty(ref medalBorderBrush, record.MedalGuardLevel switch
        {
            1 => settings.GetColor("GuardGovernorBrush", ColorSettingCatalog.GUARD_GOVERNOR_COLOR),
            2 => settings.GetColor("GuardAdmiralBrush", ColorSettingCatalog.GUARD_ADMIRAL_COLOR),
            3 => settings.GetColor("GuardCaptainBrush", ColorSettingCatalog.GUARD_CAPTAIN_COLOR),
            _ => Brushes.Transparent
        }, nameof(MedalBorderBrush));
        NotifyChange(hadNamePinyin, HasNamePinyin, nameof(HasNamePinyin));
        NotifyChange(hadDetailPinyin, HasDetailPinyin, nameof(HasDetailPinyin));
        NotifyChange(hadContentPinyin, HasContentPinyin, nameof(HasContentPinyin));
        NotifyChange(nameWasPending, IsNamePinyinPending, nameof(IsNamePinyinPending));
        NotifyChange(contentWasPending, IsContentPinyinPending, nameof(IsContentPinyinPending));
        NotifyChange(hadMedal, HasMedal, nameof(HasMedal));
        NotifyChange(hadTranslation, HasTranslation, nameof(HasTranslation));
        NotifyChange(wasTranslating, IsTranslating, nameof(IsTranslating));
        NotifyChange(showedTranslation, ShowTranslation, nameof(ShowTranslation));
        NotifyChange(showedOriginal, ShowOriginal, nameof(ShowOriginal));
    }

    private void NotifyChange<T>(T previous, T current, string propertyName)
    {
        if (!EqualityComparer<T>.Default.Equals(previous, current))
        {
            OnPropertyChanged(propertyName);
        }
    }

    private static bool ContainsChinese(string text)
    {
        return text.Any(character => character is >= '\u4E00' and <= '\u9FFF');
    }

    private static bool IsContentPinyinEnabled(MessageKind kind, MessageDisplaySettings displaySettings)
    {
        return kind switch
        {
            MessageKind.Danmu => displaySettings.ShowDanmuPinyin,
            MessageKind.SuperChat => displaySettings.ShowSuperChatPinyin,
            MessageKind.Gift => displaySettings.ShowGiftPinyin,
            _ => false
        };
    }

    private IReadOnlyList<PinyinAnnotation> GetContentAnnotations(MessageRecord record)
    {
        if (record.Kind == MessageKind.Gift)
        {
            return HasDetailPinyin ? pinyin.GetAnnotations(FormatDetail()) : [];
        }
        return pinyin.GetAnnotations(record.Content);
    }

    private string FormatDetail()
    {
        return Record.Kind switch
        {
            MessageKind.Gift => $"{Record.GiftName} ×{Record.Quantity} · ￥{Record.TotalPrice:0.##}",
            MessageKind.Guard => Localization.Get($"Panel.Guard.{Record.GuardLevel}") + $" ×{Record.Quantity}",
            _ => Record.Content
        };
    }

    private IBrush GetBorderBrush()
    {
        return Record.Kind switch
        {
            MessageKind.Gift => GetGiftBorderBrush(),
            MessageKind.Guard => Record.GuardLevel switch
            {
                1 => settings.GetColor("GuardGovernorBrush", ColorSettingCatalog.GUARD_GOVERNOR_COLOR),
                2 => settings.GetColor("GuardAdmiralBrush", ColorSettingCatalog.GUARD_ADMIRAL_COLOR),
                _ => settings.GetColor("GuardCaptainBrush", ColorSettingCatalog.GUARD_CAPTAIN_COLOR)
            },
            MessageKind.SuperChat => GetSuperChatBorderBrush(),
            _ => Record.IsPriority ? settings.GetColor("PriorityMessageBorderBrush", ColorSettingCatalog.PRIORITY_MESSAGE_COLOR) : Brushes.Transparent
        };
    }

    private IBrush GetGiftBorderBrush()
    {
        var definition = ColorSettingCatalog.GetGiftBorder(Record.TotalPrice);
        return settings.GetColor(definition.Key, definition.DefaultColor!);
    }

    private IBrush GetSuperChatBorderBrush()
    {
        var definition = ColorSettingCatalog.GetSuperChatBorder(Record.TotalPrice);
        var fallback = Record.IsTest || string.IsNullOrEmpty(Record.BorderColor)
            ? definition.DefaultColor!
            : Record.BorderColor;
        return settings.GetColor(definition.Key, settings.GetColorOverride("SuperChatBorderBrush", fallback));
    }

    private string GetAnnotation(string text, bool enabled)
    {
        if (!enabled)
        {
            return "";
        }
        var value = pinyin.GetPinyin(text);
        return value == text || string.IsNullOrWhiteSpace(value) ? "" : value;
    }
}
