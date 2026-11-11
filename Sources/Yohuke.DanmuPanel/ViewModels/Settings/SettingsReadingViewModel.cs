using CommunityToolkit.Mvvm.ComponentModel;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;

namespace Yohuke.DanmuPanel.ViewModels.Settings;

/// <summary>保存阅读页的独立输入及各事件消息的排序草稿。</summary>
public sealed partial class SettingsReadingViewModel : ObservableObject
{
    [ObservableProperty] private decimal? messageRetentionCount;
    [ObservableProperty] private decimal? messageTextScale;
    [ObservableProperty] private decimal? giftMinimum;
    [ObservableProperty] private int giftResetPolicy;
    [ObservableProperty] private bool showUserAvatars;
    [ObservableProperty] private bool showFanMedals;
    [ObservableProperty] private bool hideEmoticonDanmu;
    [ObservableProperty] private bool compactDisplay;
    [ObservableProperty] private bool compactDanmuWrap;
    [ObservableProperty] private bool compactSuperChatWrap;
    [ObservableProperty] private bool autoScrollLongMessages;
    [ObservableProperty] private bool userPinyin;
    [ObservableProperty] private bool giftPinyin;
    [ObservableProperty] private bool danmuPinyin;
    [ObservableProperty] private bool superChatPinyin;
    [ObservableProperty] private int pinyinLayout;
    [ObservableProperty] private int superChatUnreadSort;
    [ObservableProperty] private int superChatReadSort;
    [ObservableProperty] private int giftUnreadSort;
    [ObservableProperty] private int giftReadSort;
    [ObservableProperty] private int guardUnreadSort;
    [ObservableProperty] private int guardReadSort;
    [ObservableProperty] private int redPocketUnreadSort;
    [ObservableProperty] private int redPocketReadSort;

    public int MaximumMessageRetentionCount => PanelSettings.MAX_MESSAGE_RETENTION_COUNT;
    public IReadOnlyList<SettingsChoiceOption> GiftResetPolicyOptions { get; } = SettingsChoiceOption.Create(
        "Settings.ComboOnce", "Settings.ComboNewQuantity", "Settings.ComboNewValue");
    public IReadOnlyList<SettingsChoiceOption> SortOrderOptions { get; } = SettingsChoiceOption.Create(
        "Settings.SortOldestFirst", "Settings.SortNewestFirst");
    public IReadOnlyList<SettingsChoiceOption> PinyinLayoutOptions { get; } = SettingsChoiceOption.Create(
        "Settings.PinyinRuby", "Settings.PinyinSeparateLine");

    public bool HasValidSorting => new[]
        {
            SuperChatUnreadSort, SuperChatReadSort, GiftUnreadSort, GiftReadSort,
            GuardUnreadSort, GuardReadSort, RedPocketUnreadSort, RedPocketReadSort
        }
        .All(value => Enum.IsDefined((MessageSortOrder)value));

    /// <summary>复制阅读设置，所有编辑只影响当前窗口的草稿。</summary>
    public SettingsReadingViewModel(PanelSettings settings)
    {
        messageRetentionCount = settings.MessageRetentionCount;
        messageTextScale = (decimal)settings.MessageTextScale * 100;
        giftMinimum = settings.Reading.GiftAutoPinMinimum;
        giftResetPolicy = (int)settings.Reading.GiftReadResetPolicy;
        showUserAvatars = settings.ShowUserAvatars;
        showFanMedals = settings.ShowFanMedals;
        hideEmoticonDanmu = settings.HideEmoticonDanmu;
        compactDisplay = settings.CompactDanmuDisplay;
        compactDanmuWrap = settings.CompactDanmuWrap;
        compactSuperChatWrap = settings.CompactSuperChatWrap;
        autoScrollLongMessages = settings.AutoScrollLongMessages;
        userPinyin = settings.ShowUserPinyin;
        giftPinyin = settings.ShowGiftPinyin;
        danmuPinyin = settings.ShowDanmuPinyin;
        superChatPinyin = settings.ShowSuperChatPinyin;
        pinyinLayout = (int)settings.PinyinLayout;
        superChatUnreadSort = (int)settings.MessageSorting.SuperChat.Unread;
        superChatReadSort = (int)settings.MessageSorting.SuperChat.Read;
        giftUnreadSort = (int)settings.MessageSorting.Gift.Unread;
        giftReadSort = (int)settings.MessageSorting.Gift.Read;
        guardUnreadSort = (int)settings.MessageSorting.Guard.Unread;
        guardReadSort = (int)settings.MessageSorting.Guard.Read;
        redPocketUnreadSort = (int)settings.MessageSorting.RedPocket.Unread;
        redPocketReadSort = (int)settings.MessageSorting.RedPocket.Read;
    }

    /// <summary>刷新显示文字，保留各消息的排序与阅读草稿。</summary>
    public void RefreshLocalization()
    {
        foreach (var option in GiftResetPolicyOptions.Concat(SortOrderOptions).Concat(PinyinLayoutOptions))
        {
            option.RefreshLocalization();
        }
    }

    /// <summary>生成各消息类型独立的排序设置。</summary>
    public MessageSortSettings CreateSorting()
    {
        return new()
        {
            SuperChat = new() { Unread = (MessageSortOrder)SuperChatUnreadSort, Read = (MessageSortOrder)SuperChatReadSort },
            Gift = new() { Unread = (MessageSortOrder)GiftUnreadSort, Read = (MessageSortOrder)GiftReadSort },
            Guard = new() { Unread = (MessageSortOrder)GuardUnreadSort, Read = (MessageSortOrder)GuardReadSort },
            RedPocket = new() { Unread = (MessageSortOrder)RedPocketUnreadSort, Read = (MessageSortOrder)RedPocketReadSort }
        };
    }
}
