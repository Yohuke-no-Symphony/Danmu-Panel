using Avalonia;
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
    private readonly IPinyinService pinyin;
    private readonly bool deferPresentation;
    private MessagePresentationSnapshot? presentation;
    private MessageTextRenderPlan? nameRenderPlan;
    private MessageTextRenderPlan? contentRenderPlan;
    private bool isPresentationPending;
    private MessageDisplaySettings settings;
    private string name = "";
    private string namePinyin = "";
    private IReadOnlyList<PinyinAnnotation> nameAnnotations = [];
    private string detailPinyin = "";
    private IReadOnlyList<PinyinAnnotation> contentPinyin = [];
    private string medal = "";
    private string medalName = "";
    private string time = "";
    private string? detail;
    private string price = "";
    private string summaryLabel = "";
    private string summaryValue = "";
    private string translation = "";
    private bool translationPending;
    private bool translationBlocked;
    private int filterStateVersion;
    private bool hasTranslatableContent;
    private bool canShowNamePinyin;
    private bool canShowContentPinyin;
    private IBrush borderBrush = Brushes.Transparent;
    private IBrush medalBackgroundBrush = Brushes.Transparent;
    private IReadOnlyList<DanmuEmoticon> emoticons = [];
    private MessageReply? reply;
    private bool isSpecialAttention;
    private bool isPinged;
    private RedPocketViewModel? redPocket;
    public MessageRecord Record { get; private set; }
    public RedPocketViewModel? RedPocket => redPocket;
    /// <summary>当前导航定位的动态提示状态，只在界面内保留。</summary>
    public bool IsPinged
    {
        get => isPinged;
        internal set => SetProperty(ref isPinged, value);
    }
    /// <summary>是否在普通弹幕时间线中展示付费事件，点击用于导航。</summary>
    public bool IsEventSummary { get; }
    /// <summary>是否显示付费事件的类型和金额标题。</summary>
    public bool HasSummaryHeader => IsEventSummary && Record.Kind != MessageKind.Gift;
    /// <summary>是否在时间线事件中显示完整正文。</summary>
    public bool HasSummaryBody => IsEventSummary && Record.Kind is MessageKind.SuperChat or MessageKind.Gift;
    /// <summary>付费事件的类型、礼物名称或大航海档位。</summary>
    public string SummaryLabel => summaryLabel;
    /// <summary>事件的人民币价值；缺少大航海价格时明确显示未知。</summary>
    public string SummaryValue => summaryValue;
    internal bool IsPresentationPending => isPresentationPending;
    internal MessageDisplaySettings PresentationSettings => settings;
    internal bool TranslationPending => translationPending;
    internal bool TranslationBlocked => translationBlocked;
    internal int FilterVersion => filterStateVersion;
    public string Name => name;
    /// <summary>当前消息用户的稳定身份，用于独立于消息行缓存头像。</summary>
    public long UserUid => Record.UserUid;
    /// <summary>正文前内联显示的回复对象，神秘人只显示匿名头像。</summary>
    public MessageReply? Reply => reply;
    /// <summary>仅显示译文时随译文呈现的回复对象。</summary>
    public MessageReply? TranslationReply => ShowOriginal ? null : Reply;
    /// <summary>当前普通弹幕是否来自全局特别关注名单中的用户。</summary>
    public bool IsSpecialAttention => isSpecialAttention;
    /// <summary>普通弹幕名称的有效身份，依次优先使用特别关注、房管和本直播间舰队身份。</summary>
    public MessageUserNameRole UserNameRole
    {
        get
        {
            if (Record.Kind != MessageKind.Danmu)
            {
                return MessageUserNameRole.Regular;
            }
            if (IsSpecialAttention)
            {
                return MessageUserNameRole.SpecialAttention;
            }
            if (Record.IsRoomAdmin)
            {
                return MessageUserNameRole.RoomAdmin;
            }
            return Record.RoomGuardLevel switch
            {
                1 => MessageUserNameRole.Governor,
                2 => MessageUserNameRole.Admiral,
                3 => MessageUserNameRole.Captain,
                _ => MessageUserNameRole.Regular
            };
        }
    }
    /// <summary>是否显示消息用户头像。</summary>
    public bool ShowUserAvatars => settings.ShowUserAvatars;
    /// <summary>是否为弹幕保留粉丝牌显示区域。</summary>
    public bool ShowFanMedals => settings.ShowFanMedals;
    public string NamePinyin => namePinyin;
    public bool HasNamePinyin => NamePinyin.Length > 0;
    public bool IsNamePinyinPending => isPresentationPending && canShowNamePinyin;
    public IReadOnlyList<PinyinAnnotation> NameAnnotations => nameAnnotations;
    public string Medal => medal;
    public string MedalName => medalName;
    public int MedalLevel => Record.MedalLevel;
    /// <summary>粉丝牌是否具有有效的舰队身份。</summary>
    public bool HasGuardMedal => Record.MedalGuardLevel is >= 1 and <= 3;
    /// <summary>舰队粉丝牌的独立等级底色。</summary>
    public IBrush MedalBackgroundBrush => medalBackgroundBrush;
    public string Time => time;
    public bool ShowPinBadge => Record.Kind == MessageKind.Danmu && Record.IsPinned;
    public bool ShowUnreadDot => !IsEventSummary && Record.Kind is MessageKind.SuperChat or MessageKind.Gift or MessageKind.Guard or MessageKind.RedPocket && !Record.IsRead;
    public string Detail => detail ??= Localization.Get($"Panel.Guard.{Record.GuardLevel}") +
        string.Create(settings.Culture, $" ×{Record.Quantity}");
    public string DetailPinyin => detailPinyin;
    public bool HasDetailPinyin => DetailPinyin.Length > 0;
    public IReadOnlyList<PinyinAnnotation> ContentPinyin => contentPinyin;
    public bool HasContentPinyin => contentPinyin.Count > 0;
    public bool IsContentPinyinPending => isPresentationPending && canShowContentPinyin;
    public PinyinLayoutMode PinyinLayout => settings.PinyinLayout;
    public string Price => price;
    public string Translation => translation;
    public bool HasTranslation => Translation.Length > 0 && !string.Equals(Translation, Record.Content, StringComparison.Ordinal);
    public bool IsTranslating => !translationBlocked && settings.TranslationEnabled && translationPending &&
        hasTranslatableContent && Translation.Length == 0;
    public bool ShowTranslation => HasTranslation || IsTranslating;
    public bool ShowOriginal => !HasTranslation || settings.TranslationDisplayMode == TranslationDisplayMode.Bilingual;
    public bool HasMedal => Medal.Length > 0;
    public bool AutoScrollLongMessages => settings.AutoScrollLongMessages;
    public IBrush BorderBrush => borderBrush;
    /// <summary>按消息类型配置的完整边框粗细。</summary>
    public Thickness BorderThickness => new(settings.GetBorderThickness(Record.Kind));
    public IReadOnlyList<DanmuEmoticon> Emoticons
    {
        get => emoticons;
        set
        {
            if (!ReferenceEquals(emoticons, value))
            {
                var plan = value.Count > 0 || contentPinyin.Count > 0
                    ? MessageTextRenderPlan.Create(Detail, value, contentPinyin) : null;
                ApplyEmoticons(value, plan);
            }
        }
    }

    internal void ApplyEmoticons(IReadOnlyList<DanmuEmoticon> value, MessageTextRenderPlan? plan)
    {
        contentRenderPlan = plan;
        SetProperty(ref emoticons, value, nameof(Emoticons));
    }

    internal MessageTextRenderPlan? GetTextRenderPlan(string content, IReadOnlyList<DanmuEmoticon>? images,
        IReadOnlyList<PinyinAnnotation>? annotations)
    {
        if (nameRenderPlan?.Matches(content, images, annotations) == true)
        {
            return nameRenderPlan;
        }
        return contentRenderPlan?.Matches(content, images, annotations) == true ? contentRenderPlan : null;
    }

    /// <summary>创建单行显示投影。</summary>
    public MessageItemViewModel(MessageRecord record, PanelSettings settings, IPinyinService pinyin,
        bool isEventSummary = false)
        : this(MessageRowProjection.Create(record, MessageDisplaySettings.Capture(settings), false, isEventSummary), pinyin)
    {
    }

    private MessageItemViewModel(MessageRowProjection projection, IPinyinService pinyin, bool deferPresentation = false)
    {
        Record = projection.Record;
        settings = projection.Settings;
        this.pinyin = pinyin;
        IsEventSummary = projection.IsEventSummary;
        this.deferPresentation = deferPresentation;
        Update(projection, initialize: true);
    }

    /// <summary>原位更新，保留列表容器及阅读位置。</summary>
    public void Update(MessageRecord record, PanelSettings currentSettings)
    {
        Apply(record, MessageDisplaySettings.Capture(currentSettings, settings));
    }

    internal static MessageItemViewModel Create(MessageRecord record, MessageDisplaySettings settings, IPinyinService pinyin,
        bool isTranslationPending = false)
    {
        return new(MessageRowProjection.Create(record, settings, isTranslationPending), pinyin);
    }

    internal static MessageItemViewModel CreateDeferred(MessageRecord record, MessageDisplaySettings settings, IPinyinService pinyin,
        bool isTranslationPending, bool isEventSummary = false)
    {
        return CreateDeferred(MessageRowProjection.Create(record, settings, isTranslationPending, isEventSummary), pinyin);
    }

    internal static MessageItemViewModel CreateDeferred(MessageRowProjection projection, IPinyinService pinyin)
    {
        return new(projection, pinyin, deferPresentation: true);
    }

    internal bool ApplyPresentation(MessagePresentationSnapshot value)
    {
        if (!value.Matches(Record, settings.PresentationOptions) ||
            !ReferenceEquals(value.Emoticons, emoticons) && (emoticons.Count > 0 || value.Emoticons is { Count: > 0 }))
        {
            return false;
        }
        presentation = value;
        nameRenderPlan = value.NameRenderPlan;
        contentRenderPlan = value.ContentRenderPlan;
        var hadName = HasNamePinyin;
        var hadDetail = HasDetailPinyin;
        var hadContent = HasContentPinyin;
        var nameWasPending = IsNamePinyinPending;
        var contentWasPending = IsContentPinyinPending;
        SetProperty(ref namePinyin, value.NamePinyin, nameof(NamePinyin));
        SetProperty(ref nameAnnotations, value.NameAnnotations, nameof(NameAnnotations));
        SetProperty(ref detailPinyin, value.DetailPinyin, nameof(DetailPinyin));
        SetProperty(ref contentPinyin, value.ContentPinyin, nameof(ContentPinyin));
        isPresentationPending = false;
        NotifyChange(nameWasPending, IsNamePinyinPending, nameof(IsNamePinyinPending));
        NotifyChange(contentWasPending, IsContentPinyinPending, nameof(IsContentPinyinPending));
        NotifyChange(hadName, HasNamePinyin, nameof(HasNamePinyin));
        NotifyChange(hadDetail, HasDetailPinyin, nameof(HasDetailPinyin));
        NotifyChange(hadContent, HasContentPinyin, nameof(HasContentPinyin));
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

    internal void UpdateSpecialAttention(bool followed)
    {
        var previousRole = UserNameRole;
        if (SetProperty(ref isSpecialAttention, Record.Kind == MessageKind.Danmu && followed, nameof(IsSpecialAttention)))
        {
            NotifyChange(previousRole, UserNameRole, nameof(UserNameRole));
        }
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
        if (ReferenceEquals(Record, record) && ReferenceEquals(settings, currentSettings) &&
            filterStateVersion == currentSettings.DanmuFilter.StateVersion && translationPending == isTranslationPending)
        {
            return;
        }

        Apply(MessageRowProjection.Create(record, currentSettings, isTranslationPending, IsEventSummary));
    }

    internal void Apply(MessageRowProjection projection)
    {
        if (ReferenceEquals(Record, projection.Record) && ReferenceEquals(settings, projection.Settings) &&
            filterStateVersion == projection.FilterVersion && translationPending == projection.TranslationPending &&
            translationBlocked == projection.TranslationBlocked && isSpecialAttention == projection.IsSpecialAttention)
        {
            return;
        }

        Update(projection, initialize: false);
    }

    private void Update(MessageRowProjection projection, bool initialize)
    {
        var record = projection.Record;
        var previous = Record;
        var previousSettings = settings;
        var hadNamePinyin = HasNamePinyin;
        var hadDetailPinyin = HasDetailPinyin;
        var hadContentPinyin = HasContentPinyin;
        var nameWasPending = IsNamePinyinPending;
        var contentWasPending = IsContentPinyinPending;
        var couldShowContentPinyin = canShowContentPinyin;
        var hadMedal = HasMedal;
        var hadGuardMedal = HasGuardMedal;
        var hadTranslation = HasTranslation;
        var wasTranslating = IsTranslating;
        var showedTranslation = ShowTranslation;
        var showedOriginal = ShowOriginal;
        var previousTranslationReply = TranslationReply;
        var showedPin = ShowPinBadge;
        var showedUnread = ShowUnreadDot;
        var hadSummaryHeader = HasSummaryHeader;
        var hadSummaryBody = HasSummaryBody;
        var wasSpecialAttention = IsSpecialAttention;
        var previousUserNameRole = UserNameRole;
        var previousBorderThickness = BorderThickness;
        Record = record;
        settings = projection.Settings;
        translationPending = projection.TranslationPending;
        filterStateVersion = projection.FilterVersion;
        translationBlocked = projection.TranslationBlocked;
        hasTranslatableContent = projection.HasTranslatableContent;
        canShowNamePinyin = projection.CanShowNamePinyin;
        canShowContentPinyin = projection.CanShowContentPinyin;
        isSpecialAttention = projection.IsSpecialAttention;
        SetProperty(ref reply, projection.Reply, nameof(Reply));

        var nameChanged = SetProperty(ref name, projection.Name, nameof(Name));
        NotifyChange(previous.UserUid, record.UserUid, nameof(UserUid));
        NotifyChange(wasSpecialAttention, IsSpecialAttention, nameof(IsSpecialAttention));
        NotifyChange(previousUserNameRole, UserNameRole, nameof(UserNameRole));
        NotifyChange(previousSettings.ShowUserAvatars, settings.ShowUserAvatars, nameof(ShowUserAvatars));
        NotifyChange(previousSettings.ShowFanMedals, settings.ShowFanMedals, nameof(ShowFanMedals));
        NotifyChange(hadGuardMedal, HasGuardMedal, nameof(HasGuardMedal));
        NotifyChange(previous.MedalLevel, record.MedalLevel, nameof(MedalLevel));
        NotifyChange(showedPin, ShowPinBadge, nameof(ShowPinBadge));
        NotifyChange(showedUnread, ShowUnreadDot, nameof(ShowUnreadDot));
        NotifyChange(hadSummaryHeader, HasSummaryHeader, nameof(HasSummaryHeader));
        NotifyChange(hadSummaryBody, HasSummaryBody, nameof(HasSummaryBody));
        NotifyChange(previousSettings.AutoScrollLongMessages, settings.AutoScrollLongMessages, nameof(AutoScrollLongMessages));
        NotifyChange(previousSettings.PinyinLayout, settings.PinyinLayout, nameof(PinyinLayout));

        if (!deferPresentation && (initialize || nameChanged || previousSettings.ShowUserPinyin != settings.ShowUserPinyin))
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
            couldShowContentPinyin != canShowContentPinyin))
        {
            SetProperty(ref contentPinyin, canShowContentPinyin ? GetContentAnnotations(projection) : [], nameof(ContentPinyin));
        }
        if (deferPresentation && presentation?.Matches(record, settings.PresentationOptions) != true)
        {
            presentation = null;
            nameRenderPlan = null;
            if (contentRenderPlan?.Matches(projection.Detail ?? "", emoticons, null) != true)
            {
                contentRenderPlan = null;
            }
            SetProperty(ref namePinyin, "", nameof(NamePinyin));
            SetProperty(ref nameAnnotations, Array.Empty<PinyinAnnotation>(), nameof(NameAnnotations));
            SetProperty(ref detailPinyin, "", nameof(DetailPinyin));
            SetProperty(ref contentPinyin, Array.Empty<PinyinAnnotation>(), nameof(ContentPinyin));
            isPresentationPending = canShowNamePinyin || canShowContentPinyin;
        }
        SetProperty(ref medal, projection.Medal, nameof(Medal));
        SetProperty(ref medalName, projection.MedalName, nameof(MedalName));
        var cultureChanged = previousSettings.CultureName != settings.CultureName;
        if (record.Kind == MessageKind.RedPocket && !IsEventSummary)
        {
            redPocket ??= new();
            redPocket.ShowUserAvatars = settings.ShowUserAvatars;
            if (initialize || previous.RedPocket != record.RedPocket || previousSettings.LanguageId != settings.LanguageId || cultureChanged)
            {
                redPocket.Update(record.RedPocket);
                OnPropertyChanged(nameof(RedPocket));
            }
        }
        SetProperty(ref time, projection.Time, nameof(Time));
        SetProperty(ref detail, projection.Detail, nameof(Detail));
        SetProperty(ref price, projection.Price, nameof(Price));
        SetProperty(ref summaryLabel, projection.SummaryLabel, nameof(SummaryLabel));
        SetProperty(ref summaryValue, projection.SummaryValue, nameof(SummaryValue));
        SetProperty(ref translation, projection.Translation, nameof(Translation));
        SetProperty(ref borderBrush, GetBorderBrush(), nameof(BorderBrush));
        NotifyChange(previousBorderThickness, BorderThickness, nameof(BorderThickness));
        SetProperty(ref medalBackgroundBrush, record.MedalGuardLevel switch
        {
            1 => settings.GetColor("MedalGovernorBackgroundBrush", ColorSettingCatalog.GUARD_GOVERNOR_COLOR),
            2 => settings.GetColor("MedalAdmiralBackgroundBrush", ColorSettingCatalog.GUARD_ADMIRAL_COLOR),
            3 => settings.GetColor("MedalCaptainBackgroundBrush", ColorSettingCatalog.GUARD_CAPTAIN_COLOR),
            _ => Brushes.Transparent
        }, nameof(MedalBackgroundBrush));
        NotifyChange(hadNamePinyin, HasNamePinyin, nameof(HasNamePinyin));
        NotifyChange(hadDetailPinyin, HasDetailPinyin, nameof(HasDetailPinyin));
        NotifyChange(hadContentPinyin, HasContentPinyin, nameof(HasContentPinyin));
        NotifyChange(nameWasPending, IsNamePinyinPending, nameof(IsNamePinyinPending));
        NotifyChange(contentWasPending, IsContentPinyinPending, nameof(IsContentPinyinPending));
        NotifyChange(hadMedal, HasMedal, nameof(HasMedal));
        NotifyChange(hadTranslation, HasTranslation, nameof(HasTranslation));
        NotifyChange(wasTranslating, IsTranslating, nameof(IsTranslating));
        NotifyChange(showedTranslation, ShowTranslation, nameof(ShowTranslation));
        NotifyChange(previousTranslationReply, TranslationReply, nameof(TranslationReply));
        NotifyChange(showedOriginal, ShowOriginal, nameof(ShowOriginal));
    }

    private void NotifyChange<T>(T previous, T current, string propertyName)
    {
        if (!EqualityComparer<T>.Default.Equals(previous, current))
        {
            OnPropertyChanged(propertyName);
        }
    }

    private IReadOnlyList<PinyinAnnotation> GetContentAnnotations(MessageRowProjection projection)
    {
        if (projection.Record.Kind == MessageKind.Gift)
        {
            return HasDetailPinyin ? pinyin.GetAnnotations(projection.Detail!) : [];
        }
        return pinyin.GetAnnotations(projection.Record.Content);
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
