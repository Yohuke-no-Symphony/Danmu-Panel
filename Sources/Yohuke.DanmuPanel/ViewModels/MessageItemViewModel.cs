using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Models.Workspace;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.Services.Appearance;

namespace Yohuke.DanmuPanel.ViewModels;

/// <summary>消息的显示投影，保留原始名称与阅读状态。</summary>
public sealed class MessageItemViewModel : ObservableObject
{
    private const int MAX_MEDAL_NAME_TEXT_ELEMENTS = 3;
    private readonly IPinyinService pinyin;
    private MessageDisplaySettings settings;
    private string namePinyin = "";
    private string detailPinyin = "";
    private string medal = "";
    private string medalName = "";
    private string time = "";
    private string? detail;
    private string price = "";
    private string translation = "";
    private IBrush borderBrush = Brushes.Transparent;
    private IBrush medalBorderBrush = Brushes.Transparent;
    private IReadOnlyList<DanmuEmoticon> emoticons = [];
    public MessageRecord Record { get; private set; }
    public string Name => Record.UserName;
    public string NamePinyin => namePinyin;
    public bool HasNamePinyin => NamePinyin.Length > 0;
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
    public string Price => price;
    public string Translation => translation;
    public bool HasTranslation => Translation.Length > 0;
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

    private MessageItemViewModel(MessageRecord record, MessageDisplaySettings settings, IPinyinService pinyin)
    {
        Record = record;
        this.settings = settings;
        this.pinyin = pinyin;
        Update(record, settings, true);
    }

    /// <summary>原位更新，保留列表容器及阅读位置。</summary>
    public void Update(MessageRecord record, PanelSettings currentSettings)
    {
        Apply(record, MessageDisplaySettings.Capture(currentSettings, settings));
    }

    internal static MessageItemViewModel Create(MessageRecord record, MessageDisplaySettings settings, IPinyinService pinyin)
    {
        return new(record, settings, pinyin);
    }

    internal void Apply(MessageRecord record, MessageDisplaySettings currentSettings)
    {
        Update(record, currentSettings, false);
    }

    private void Update(MessageRecord record, MessageDisplaySettings currentSettings, bool initialize)
    {
        var previous = Record;
        var previousSettings = settings;
        var hadNamePinyin = HasNamePinyin;
        var hadDetailPinyin = HasDetailPinyin;
        var hadMedal = HasMedal;
        var hadTranslation = HasTranslation;
        var showedOriginal = ShowOriginal;
        var showedPin = ShowPinBadge;
        var showedUnread = ShowUnreadDot;
        Record = record;
        settings = currentSettings;

        NotifyChange(previous.UserName, record.UserName, nameof(Name));
        NotifyChange(previous.MedalLevel, record.MedalLevel, nameof(MedalLevel));
        NotifyChange(showedPin, ShowPinBadge, nameof(ShowPinBadge));
        NotifyChange(showedUnread, ShowUnreadDot, nameof(ShowUnreadDot));
        NotifyChange(previousSettings.AutoScrollLongMessages, settings.AutoScrollLongMessages, nameof(AutoScrollLongMessages));

        if (initialize || previous.UserName != record.UserName || previousSettings.ShowUserPinyin != settings.ShowUserPinyin)
        {
            SetProperty(ref namePinyin, GetAnnotation(Name, settings.ShowUserPinyin), nameof(NamePinyin));
        }
        if (initialize || previous.Kind != record.Kind || previous.GiftName != record.GiftName ||
            previousSettings.ShowGiftPinyin != settings.ShowGiftPinyin)
        {
            SetProperty(ref detailPinyin, record.Kind == MessageKind.Gift
                ? GetAnnotation(record.GiftName, settings.ShowGiftPinyin) : "", nameof(DetailPinyin));
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
        NotifyChange(hadMedal, HasMedal, nameof(HasMedal));
        NotifyChange(hadTranslation, HasTranslation, nameof(HasTranslation));
        NotifyChange(showedOriginal, ShowOriginal, nameof(ShowOriginal));
    }

    private void NotifyChange<T>(T previous, T current, string propertyName)
    {
        if (!EqualityComparer<T>.Default.Equals(previous, current))
        {
            OnPropertyChanged(propertyName);
        }
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
