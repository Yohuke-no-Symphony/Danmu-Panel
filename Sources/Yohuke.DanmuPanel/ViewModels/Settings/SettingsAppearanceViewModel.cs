using CommunityToolkit.Mvvm.ComponentModel;
using Yohuke.DanmuPanel.Models.Appearance;
using Yohuke.DanmuPanel.Models.State;

namespace Yohuke.DanmuPanel.ViewModels.Settings;

/// <summary>保存外观页的输入草稿及即时偏好选择。</summary>
public sealed partial class SettingsAppearanceViewModel : ObservableObject
{
    internal static readonly string[] languageIds = ["system", "zh-CN", "en-US", "ja-JP"];
    internal static readonly string[] themeIds = ["system", "light", "dark"];

    [ObservableProperty] private int languageIndex;
    [ObservableProperty] private int themeIndex;
    [ObservableProperty] private bool useWindowMenuOnMac;
    [ObservableProperty] private decimal? superChatBorderThickness;
    [ObservableProperty] private decimal? giftBorderThickness;
    [ObservableProperty] private decimal? guardBorderThickness;
    [ObservableProperty] private decimal? redPocketBorderThickness;

    public bool SupportsNativeMenus { get; }
    public IReadOnlyList<SettingsChoiceOption> LanguageOptions { get; } = SettingsChoiceOption.Create(
        "Settings.FollowSystem", "Settings.Chinese", "Settings.English", "Settings.Japanese");
    public IReadOnlyList<SettingsChoiceOption> ThemeOptions { get; } = SettingsChoiceOption.Create(
        "Settings.FollowSystem", "Settings.Light", "Settings.Dark");
    public decimal MinimumBorderThickness => (decimal)MessageBorderSettings.MIN_THICKNESS;
    public decimal MaximumBorderThickness => (decimal)MessageBorderSettings.MAX_THICKNESS;

    /// <summary>复制输入值，避免边框编辑提前修改运行配置。</summary>
    public SettingsAppearanceViewModel(PanelSettings settings, bool supportsNativeMenus)
    {
        languageIndex = Math.Max(0, Array.IndexOf(languageIds, settings.Language));
        themeIndex = Math.Max(0, Array.IndexOf(themeIds, settings.Theme));
        useWindowMenuOnMac = settings.UseWindowMenuOnMac;
        superChatBorderThickness = (decimal)settings.MessageBorders.SuperChat;
        giftBorderThickness = (decimal)settings.MessageBorders.Gift;
        guardBorderThickness = (decimal)settings.MessageBorders.Guard;
        redPocketBorderThickness = (decimal)settings.MessageBorders.RedPocket;
        SupportsNativeMenus = supportsNativeMenus;
    }

    /// <summary>刷新下拉显示文字，保留选中索引与选项实例。</summary>
    public void RefreshLocalization()
    {
        foreach (var option in LanguageOptions.Concat(ThemeOptions))
        {
            option.RefreshLocalization();
        }
    }

    /// <summary>仅在全部输入合法时创建独立的边框设置。</summary>
    public bool TryCreateBorders(out MessageBorderSettings borders)
    {
        borders = null!;
        if (SuperChatBorderThickness is not { } superChat || GiftBorderThickness is not { } gift ||
            GuardBorderThickness is not { } guard || RedPocketBorderThickness is not { } redPocket ||
            superChat < MinimumBorderThickness || superChat > MaximumBorderThickness ||
            gift < MinimumBorderThickness || gift > MaximumBorderThickness ||
            guard < MinimumBorderThickness || guard > MaximumBorderThickness ||
            redPocket < MinimumBorderThickness || redPocket > MaximumBorderThickness)
        {
            return false;
        }
        borders = new()
        {
            SuperChat = (double)superChat,
            Gift = (double)gift,
            Guard = (double)guard,
            RedPocket = (double)redPocket
        };
        return true;
    }
}
