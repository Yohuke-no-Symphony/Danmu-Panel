using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services.Updates;

namespace Yohuke.DanmuPanel.ViewModels.Settings;

/// <summary>保存设置窗口导航、操作状态、命令与各页草稿。</summary>
public sealed partial class SettingsDialogViewModel : ObservableObject
{
    private static readonly string[] sections = ["Appearance", "Reading", "Blocking", "SpecialAttention", "Translation", "Accounts", "Data", "Updates"];
    [ObservableProperty] private string selectedSection = "Appearance";
    [ObservableProperty] private string status = "";
    [ObservableProperty] private bool isStatusSuccess;
    [ObservableProperty] private bool isOperating;
    [ObservableProperty] private bool checkForUpdatesOnStartup;

    public SettingsAppearanceViewModel Appearance { get; }
    public SettingsReadingViewModel Reading { get; }
    public SettingsTranslationViewModel Translation { get; }
    public SettingsDataViewModel Data { get; }
    public IRelayCommand<string> SelectSectionCommand { get; }
    public IAsyncRelayCommand SaveCommand { get; }
    public IRelayCommand ImportCommand { get; }
    public IRelayCommand ExportCommand { get; }
    public IRelayCommand ClearCurrentCommand { get; }
    public IRelayCommand ClearAllCommand { get; }
    public IAsyncRelayCommand ClearCacheCommand { get; }
    public IRelayCommand CheckForUpdatesCommand { get; }
    public string CurrentVersion => AppUpdateVersion.Current.ToString();
    public bool IsInteractive => !IsOperating;
    public bool HasStatus => !string.IsNullOrEmpty(Status);
    public string SectionDescription => Localization.Get("Settings." + SelectedSection + "Description");
    public bool IsAppearanceSelected => SelectedSection == "Appearance";
    public bool IsReadingSelected => SelectedSection == "Reading";
    public bool IsBlockingSelected => SelectedSection == "Blocking";
    public bool IsSpecialAttentionSelected => SelectedSection == "SpecialAttention";
    public bool IsTranslationSelected => SelectedSection == "Translation";
    public bool IsAccountsSelected => SelectedSection == "Accounts";
    public bool IsDataSelected => SelectedSection == "Data";
    public bool IsUpdatesSelected => SelectedSection == "Updates";

    /// <summary>创建不持有控件的页面状态及由宿主提供的服务命令。</summary>
    public SettingsDialogViewModel(PanelSettings settings, bool supportsNativeMenus, Func<Task> save,
        Action import, Action export, Action<bool> clear, Func<Task> clearCache, Action checkForUpdates,
        SettingsDataViewModel data)
    {
        Appearance = new(settings, supportsNativeMenus);
        Reading = new(settings);
        Translation = new(settings.Translation);
        Data = data;
        checkForUpdatesOnStartup = settings.CheckForUpdatesOnStartup;
        SelectSectionCommand = new RelayCommand<string>(section => SelectedSection = section!, section => section is not null && sections.Contains(section));
        SaveCommand = new AsyncRelayCommand(save);
        ImportCommand = new RelayCommand(import);
        ExportCommand = new RelayCommand(export);
        ClearCurrentCommand = new RelayCommand(() => clear(false));
        ClearAllCommand = new RelayCommand(() => clear(true));
        ClearCacheCommand = new AsyncRelayCommand(clearCache);
        CheckForUpdatesCommand = new RelayCommand(checkForUpdates);
    }

    /// <summary>刷新当前分类说明及保留实例的语言选项。</summary>
    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(SectionDescription));
        Appearance.RefreshLocalization();
        Reading.RefreshLocalization();
        Translation.RefreshLocalization();
        Data.RefreshLocalization();
    }

    partial void OnSelectedSectionChanged(string value)
    {
        OnPropertyChanged(nameof(SectionDescription));
        OnPropertyChanged(nameof(IsAppearanceSelected));
        OnPropertyChanged(nameof(IsReadingSelected));
        OnPropertyChanged(nameof(IsBlockingSelected));
        OnPropertyChanged(nameof(IsSpecialAttentionSelected));
        OnPropertyChanged(nameof(IsTranslationSelected));
        OnPropertyChanged(nameof(IsAccountsSelected));
        OnPropertyChanged(nameof(IsDataSelected));
        OnPropertyChanged(nameof(IsUpdatesSelected));
    }

    partial void OnStatusChanged(string value)
    {
        OnPropertyChanged(nameof(HasStatus));
    }

    partial void OnIsOperatingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsInteractive));
    }
}
