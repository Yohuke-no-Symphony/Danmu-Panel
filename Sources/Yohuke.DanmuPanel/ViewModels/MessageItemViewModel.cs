using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Models.Workspace;
using Yohuke.DanmuPanel.Services.Translation;

namespace Yohuke.DanmuPanel.ViewModels;

/// <summary>消息的显示投影，保留原始名称与阅读状态。</summary>
public sealed class MessageItemViewModel : ObservableObject
{
    private const int MAX_MEDAL_NAME_TEXT_ELEMENTS = 3;
    private readonly IPinyinService pinyin;
    private PanelSettings settings;
    private IReadOnlyList<DanmuEmoticon> emoticons = [];
    public MessageRecord Record { get; private set; }
    public bool IsTest => Record.IsTest;
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
    public Avalonia.Media.IBrush MedalBorderBrush => Avalonia.Media.Brush.Parse(Record.MedalGuardLevel switch
    {
        1 => "#E9A23B", 2 => "#A46EE8", 3 => "#4F8FDD", _ => "Transparent"
    });
    public string Time => Record.Time.ToLocalTime().ToString("HH:mm:ss");
    public string State => Record.IsPinned ? "📌" : Record.Kind != MessageKind.Danmu && !Record.IsRead ? "●" : "";
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
    public Avalonia.Media.IBrush BorderBrush => Avalonia.Media.Brush.Parse(Record.Kind switch
    {
        MessageKind.Guard => Record.GuardLevel switch { 1 => "#E9A23B", 2 => "#A46EE8", _ => "#4F8FDD" },
        MessageKind.SuperChat => string.IsNullOrEmpty(Record.BorderColor) ? "#2A60B2" : Record.BorderColor,
        _ => Record.IsPriority ? "#5B9BD5" : "Transparent"
    });
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
