using System.Globalization;
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
    private PanelSettings settings;
    private IReadOnlyList<DanmuEmoticon> emoticons = [];
    public MessageRecord Record { get; private set; }
    public string Name => Record.UserName;
    public string NamePinyin => GetAnnotation(Name, settings.ShowUserPinyin);
    public bool HasNamePinyin => NamePinyin.Length > 0;
    public string Medal => Record.MedalLevel > 0 ? $"{Record.MedalName} {Record.MedalLevel}" : "";
    public string MedalName
    {
        get
        {
            var name = new StringInfo(Record.MedalName);
            return name.LengthInTextElements <= MAX_MEDAL_NAME_TEXT_ELEMENTS
                ? Record.MedalName
                : name.SubstringByTextElements(0, MAX_MEDAL_NAME_TEXT_ELEMENTS);
        }
    }
    public int MedalLevel => Record.MedalLevel;
    public Avalonia.Media.IBrush MedalBorderBrush => Record.MedalGuardLevel switch
    {
        1 => GetColor("GuardGovernorBrush", ColorSettingCatalog.GUARD_GOVERNOR_COLOR),
        2 => GetColor("GuardAdmiralBrush", ColorSettingCatalog.GUARD_ADMIRAL_COLOR),
        3 => GetColor("GuardCaptainBrush", ColorSettingCatalog.GUARD_CAPTAIN_COLOR),
        _ => Avalonia.Media.Brushes.Transparent
    };
    public string Time => Record.Time.ToLocalTime().ToString("HH:mm:ss");
    public bool ShowPinBadge => Record.Kind == MessageKind.Danmu && Record.IsPinned;
    public bool ShowUnreadDot => Record.Kind is MessageKind.SuperChat or MessageKind.Gift or MessageKind.Guard && !Record.IsRead;
    public string Detail => Record.Kind switch
    {
        MessageKind.Gift => $"{Record.GiftName} ×{Record.Quantity} · ￥{Record.TotalPrice:0.##}",
        MessageKind.Guard => Localization.Get($"Panel.Guard.{Record.GuardLevel}") + $" ×{Record.Quantity}",
        _ => Record.Content
    };
    public string DetailPinyin => Record.Kind == MessageKind.Gift
        ? GetAnnotation(Record.GiftName, settings.ShowGiftPinyin) : "";
    public bool HasDetailPinyin => DetailPinyin.Length > 0;
    public string Price => Record.Kind == MessageKind.SuperChat ? $"￥{Record.TotalPrice:0.##}" : "";
    public string Translation => Record.TranslationFingerprint == TranslationRequestFactory.GetConfigurationFingerprint(settings.Translation)
        ? Record.Translation ?? "" : "";
    public bool HasTranslation => Translation.Length > 0;
    public bool ShowOriginal => !HasTranslation || settings.Translation.DisplayMode == TranslationDisplayMode.Bilingual;
    public bool HasMedal => Medal.Length > 0;
    public bool AutoScrollLongMessages => settings.AutoScrollLongMessages;
    public Avalonia.Media.IBrush BorderBrush => Record.Kind switch
    {
        MessageKind.Gift => GetGiftBorderBrush(),
        MessageKind.Guard => Record.GuardLevel switch
        {
            1 => GetColor("GuardGovernorBrush", ColorSettingCatalog.GUARD_GOVERNOR_COLOR),
            2 => GetColor("GuardAdmiralBrush", ColorSettingCatalog.GUARD_ADMIRAL_COLOR),
            _ => GetColor("GuardCaptainBrush", ColorSettingCatalog.GUARD_CAPTAIN_COLOR)
        },
        MessageKind.SuperChat => GetSuperChatBorderBrush(),
        _ => Record.IsPriority ? GetColor("PriorityMessageBorderBrush", ColorSettingCatalog.PRIORITY_MESSAGE_COLOR) : Avalonia.Media.Brushes.Transparent
    };
    public IReadOnlyList<DanmuEmoticon> Emoticons
    {
        get => emoticons;
        set => SetProperty(ref emoticons, value);
    }

    /// <summary>创建单行显示投影。</summary>
    public MessageItemViewModel(MessageRecord record, PanelSettings settings, IPinyinService pinyin)
    {
        Record = record;
        this.settings = settings;
        this.pinyin = pinyin;
    }

    /// <summary>原位更新，保留列表容器及阅读位置。</summary>
    public void Update(MessageRecord record, PanelSettings currentSettings)
    {
        Record = record;
        settings = currentSettings;
        OnPropertyChanged(string.Empty);
    }

    private Avalonia.Media.IBrush GetColor(string key, string fallback)
    {
        return Avalonia.Media.Brush.Parse(settings.ColorScheme.Overrides.GetValueOrDefault(key, fallback));
    }

    private Avalonia.Media.IBrush GetGiftBorderBrush()
    {
        var definition = ColorSettingCatalog.GetGiftBorder(Record.TotalPrice);
        return GetColor(definition.Key, definition.DefaultColor!);
    }

    private Avalonia.Media.IBrush GetSuperChatBorderBrush()
    {
        var definition = ColorSettingCatalog.GetSuperChatBorder(Record.TotalPrice);
        var fallback = Record.IsTest || string.IsNullOrEmpty(Record.BorderColor)
            ? definition.DefaultColor!
            : Record.BorderColor;
        return GetColor(definition.Key, settings.ColorScheme.Overrides.GetValueOrDefault("SuperChatBorderBrush", fallback));
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
