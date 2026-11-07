using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.Appearance;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Appearance;
using Yohuke.DanmuPanel.Services.Messages;
using Yohuke.DanmuPanel.Services.Settings;
using Yohuke.DanmuPanel.ViewModels.Settings;

namespace Yohuke.DanmuPanel.Views.Settings;

/// <summary>协调设置页面草稿、即时偏好及设置导入导出。</summary>
public sealed class SettingsView : UserControl
{
    private readonly SettingsDialogSurface surface = new();
    private readonly SettingsTranslationPage translationPage;
    private readonly IReadOnlyDictionary<string, Control> pages;
    private readonly ColorSchemeEditor colorSchemeEditor;
    private readonly PanelRuntime runtime;
    private readonly SettingsShareService sharing = new();
    private readonly ISettingsTransferDialogs transferDialogs;
    private readonly Control accountsView;
    private readonly Func<string, Task<bool>> openDataDirectory;

    internal SettingsDialogViewModel Model { get; }
    internal SpecialAttentionEditor SpecialAttentionEditor { get; }
    internal DanmuRegexEditor DanmuRegexEditor { get; }
    internal Task PendingMenuPreferenceSave { get; private set; } = Task.CompletedTask;
    internal Task PendingUpdatePreferenceSave { get; private set; } = Task.CompletedTask;
    internal Task PendingSettingsTransfer { get; private set; } = Task.CompletedTask;

    public event Action? Saved;
    public event Action<bool>? ClearRequested;

    /// <summary>通知应用即时切换 macOS 菜单位置。</summary>
    public event Action<bool>? WindowMenuOnMacChanged;

    /// <summary>请求在当前设置窗口中手动检查更新，不保存其他页的草稿。</summary>
    public event Action? CheckForUpdatesRequested;

    /// <summary>通知应用即时启用或取消启动更新检查。</summary>
    public event Action<bool>? StartupUpdatePreferenceChanged;

    /// <summary>创建设置面板，账号界面由应用提供。</summary>
    public SettingsView(PanelRuntime runtime, Control accountsView)
        : this(runtime, accountsView, OperatingSystem.IsMacOS())
    {
    }

    internal SettingsView(PanelRuntime runtime, Control accountsView, bool supportsNativeMenus,
        ISettingsTransferDialogs? transferDialogs = null, IBilibiliUserSearchService? userSearch = null,
        IUserAvatarService? userAvatars = null, Func<string, Task<bool>>? openDataDirectory = null)
    {
        this.runtime = runtime;
        this.transferDialogs = transferDialogs ?? new SettingsTransferDialogs();
        this.accountsView = accountsView;
        this.openDataDirectory = openDataDirectory ?? LaunchDataDirectoryAsync;
        var cacheSize = new CacheSizeService(new(runtime.ConfigurationStore.DirectoryPath));
        var settings = runtime.Configuration.Settings;
        Model = new(settings, supportsNativeMenus, async () =>
            {
                await SaveAsync();
            },
            () => PendingSettingsTransfer = ImportSettingsAsync(),
            () => PendingSettingsTransfer = ExportSettingsAsync(),
            all => ClearRequested?.Invoke(all), ClearCacheAsync,
            () => CheckForUpdatesRequested?.Invoke(),
            new(runtime.ConfigurationStore.DirectoryPath, cacheSize.GetSizeAsync, OpenDataDirectoryAsync));
        colorSchemeEditor = new(settings, runtime.Colors,
            new HashSet<string>(StringComparer.Ordinal)
            {
                ColorSettingCatalog.SPECIAL_ATTENTION_USER_NAME_BRUSH, ColorSettingCatalog.SPECIAL_ATTENTION_CONTENT_BRUSH
            });
        SpecialAttentionEditor = new(runtime,
            colorSchemeEditor.GetInput(ColorSettingCatalog.SPECIAL_ATTENTION_USER_NAME_BRUSH),
            colorSchemeEditor.GetInput(ColorSettingCatalog.SPECIAL_ATTENTION_CONTENT_BRUSH), userSearch, userAvatars);
        DanmuRegexEditor = new(settings.DanmuRegexFilter);
        translationPage = new() { DataContext = Model.Translation };
        pages = new Dictionary<string, Control>(StringComparer.Ordinal)
        {
            ["Appearance"] = new SettingsAppearancePage { DataContext = Model.Appearance, ColorEditor = colorSchemeEditor },
            ["Reading"] = new SettingsReadingPage { DataContext = Model.Reading },
            ["Blocking"] = new SettingsBlockingPage { DataContext = Model, Editor = DanmuRegexEditor },
            ["SpecialAttention"] = new SettingsSpecialAttentionPage { DataContext = Model, Editor = SpecialAttentionEditor },
            ["Translation"] = translationPage,
            ["Accounts"] = new SettingsAccountsPage { DataContext = Model, Editor = accountsView },
            ["Data"] = new SettingsDataPage { DataContext = Model },
            ["Updates"] = new SettingsUpdatesPage { DataContext = Model }
        };
        Model.Translation.CommitEditsRequested += translationPage.CommitEdits;
        Model.Translation.TermAdded += translationPage.ScrollToTerm;
        Model.PropertyChanged += OnModelChanged;
        Model.Appearance.PropertyChanged += OnAppearanceChanged;
        colorSchemeEditor.Changed += ApplyAppearance;
        surface.DataContext = Model;
        surface.PageContent = pages[Model.SelectedSection];
        Content = surface;
        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += OnDetached;
    }

    /// <summary>校验并保存设置；失败保留窗口及输入内容。</summary>
    public async Task<bool> SaveAsync()
    {
        if (!TryBeginOperation())
        {
            return false;
        }
        SpecialAttentionSettings? previousAttention = null;
        SpecialAttentionSettings? appliedAttention = null;
        try
        {
            await Task.WhenAll(PendingMenuPreferenceSave, PendingUpdatePreferenceSave);
            if (!TryCreateDraft(out var draft))
            {
                return false;
            }
            previousAttention = runtime.Configuration.Settings.SpecialAttention;
            sharing.ApplyTo(runtime.Configuration.Settings, draft);
            appliedAttention = runtime.Configuration.Settings.SpecialAttention;
            if (accountsView is AccountSettingsView accountSettings)
            {
                await accountSettings.SaveEditsAsync();
            }
            await runtime.ApplySettingsAsync();
            SetStatus("");
            Saved?.Invoke();
            return true;
        }
        catch (Exception exception)
        {
            if (appliedAttention is not null && ReferenceEquals(runtime.Configuration.Settings.SpecialAttention, appliedAttention))
            {
                runtime.Configuration.Settings.SpecialAttention = previousAttention!;
            }
            SetStatus(Localization.Get("Settings.SaveFailed") + " · " + exception.GetType().Name);
            return false;
        }
        finally
        {
            EndOperation();
        }
    }

    internal async Task<bool> ExportSettingsAsync()
    {
        if (!TryBeginOperation())
        {
            return false;
        }
        try
        {
            if (!TryCreateDraft(out var draft))
            {
                return false;
            }
            var snapshot = sharing.CreatePackage(draft).Settings;
            await using var stream = await transferDialogs.OpenExportAsync((Window)TopLevel.GetTopLevel(this)!);
            if (stream is null)
            {
                return false;
            }
            stream.SetLength(0);
            stream.Position = 0;
            await sharing.ExportAsync(snapshot, stream);
            SetStatus(Localization.Get("Settings.Exported"), isError: false);
            return true;
        }
        catch (Exception exception)
        {
            SetStatus(Localization.Get("Settings.ExportFailed") + " · " + exception.GetType().Name);
            return false;
        }
        finally
        {
            EndOperation();
        }
    }

    internal async Task<bool> ImportSettingsAsync()
    {
        if (!TryBeginOperation())
        {
            return false;
        }
        try
        {
            var owner = (Window)TopLevel.GetTopLevel(this)!;
            await using var stream = await transferDialogs.OpenImportAsync(owner);
            if (stream is null)
            {
                return false;
            }
            PanelSettings imported;
            try
            {
                imported = await sharing.ImportAsync(stream);
            }
            catch (InvalidDataException)
            {
                SetStatus(Localization.Get("Settings.InvalidSettingsFile"));
                return false;
            }
            if (!await transferDialogs.ConfirmImportAsync(owner))
            {
                return false;
            }
            await Task.WhenAll(PendingMenuPreferenceSave, PendingUpdatePreferenceSave);
            if (accountsView is AccountSettingsView accountSettings)
            {
                await accountSettings.SaveEditsAsync();
            }
            var settings = runtime.Configuration.Settings;
            var previous = sharing.Snapshot(settings);
            sharing.ApplyTo(settings, imported);
            try
            {
                await runtime.ApplySettingsAsync();
            }
            catch (Exception exception)
            {
                sharing.RestoreTo(settings, previous);
                try
                {
                    await runtime.ApplySettingsAsync();
                }
                catch (Exception restoreException)
                {
                    SetStatus(Localization.Get("Settings.ImportRestoreFailed") + " · " + exception.GetType().Name + " / " + restoreException.GetType().Name);
                    return false;
                }
                SetStatus(Localization.Get("Settings.ImportFailed") + " · " + exception.GetType().Name);
                return false;
            }
            Localization.SetLanguage(settings.Language);
            Application.Current!.RequestedThemeVariant = settings.Theme switch
            {
                "light" => ThemeVariant.Light,
                "dark" => ThemeVariant.Dark,
                _ => ThemeVariant.Default
            };
            runtime.Colors.Apply(Application.Current, settings.ColorScheme);
            runtime.NotifyAppearanceChanged();
            WindowMenuOnMacChanged?.Invoke(settings.UseWindowMenuOnMac);
            StartupUpdatePreferenceChanged?.Invoke(settings.CheckForUpdatesOnStartup);
            SetStatus("");
            Saved?.Invoke();
            return true;
        }
        catch (Exception exception)
        {
            SetStatus(Localization.Get("Settings.ImportFailed") + " · " + exception.GetType().Name);
            return false;
        }
        finally
        {
            EndOperation();
        }
    }

    private bool TryCreateDraft(out PanelSettings draft)
    {
        draft = null!;
        var reading = Model.Reading;
        var appearance = Model.Appearance;
        var translation = Model.Translation;
        if (!reading.HasValidSorting)
        {
            SetStatus(Localization.Get("Settings.InvalidMessageSortOrder"));
            return false;
        }
        if (!appearance.TryCreateBorders(out var messageBorders))
        {
            SetStatus(Localization.Get("Settings.InvalidMessageBorderThickness"));
            return false;
        }
        if (reading.MessageRetentionCount is not { } retentionCount || retentionCount != decimal.Truncate(retentionCount) ||
            retentionCount is < 1 or > PanelSettings.MAX_MESSAGE_RETENTION_COUNT)
        {
            SetStatus(Localization.Format("Settings.InvalidMessageRetentionCount", PanelSettings.MAX_MESSAGE_RETENTION_COUNT));
            return false;
        }
        if (!DanmuRegexEditor.Model.TryCreateSettings(out var danmuRegexSettings, out var invalidRuleNumber))
        {
            SetStatus(Localization.Format("Settings.DanmuRegexInvalidPattern", invalidRuleNumber));
            return false;
        }
        if (!colorSchemeEditor.TryCommit(out var colorError))
        {
            SetStatus(colorError ?? Localization.Get("Colors.InvalidColor"));
            return false;
        }
        translationPage.CommitEdits();
        var terms = translation.Glossary.Where(entry => !string.IsNullOrWhiteSpace(entry.Source) || entry.HasTranslations).ToList();
        if (terms.Any(entry => string.IsNullOrWhiteSpace(entry.Source) || !entry.HasTranslations))
        {
            SetStatus(Localization.Get("Settings.IncompleteTerm"));
            return false;
        }
        if (translation.Enabled && (!Uri.TryCreate(translation.SelectedBaseUrl, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment) || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            string.IsNullOrWhiteSpace(translation.Model) || string.IsNullOrWhiteSpace(translation.SelectedLanguage.Code)))
        {
            SetStatus(Localization.Get("Settings.InvalidTranslation"));
            return false;
        }
        var settings = runtime.Configuration.Settings;
        draft = new()
        {
            Language = settings.Language,
            Theme = settings.Theme,
            ColorScheme = ColorSchemeService.Snapshot(settings.ColorScheme),
            MessageBorders = messageBorders,
            UseWindowMenuOnMac = settings.UseWindowMenuOnMac,
            CheckForUpdatesOnStartup = settings.CheckForUpdatesOnStartup,
            Translation = new()
            {
                Enabled = translation.Enabled,
                BaseUrl = translation.SelectedBaseUrl,
                ApiKey = translation.ApiKey ?? "",
                Model = translation.Model?.Trim() ?? "",
                TargetLanguage = translation.SelectedLanguage.Code,
                ExtraSystemPrompt = translation.ExtraPrompt ?? "",
                DisplayMode = (TranslationDisplayMode)translation.DisplayMode,
                BacklogMode = (TranslationBacklogMode)translation.BacklogMode,
                Glossary = terms.SelectMany(entry => entry.GetEntries()).ToList()
            },
            Reading = new()
            {
                GiftAutoPinMinimum = reading.GiftMinimum ?? 0,
                GiftReadResetPolicy = (GiftReadResetPolicy)reading.GiftResetPolicy
            },
            MessageSorting = reading.CreateSorting(),
            ShowUserPinyin = reading.UserPinyin,
            ShowGiftPinyin = reading.GiftPinyin,
            ShowDanmuPinyin = reading.DanmuPinyin,
            ShowSuperChatPinyin = reading.SuperChatPinyin,
            PinyinLayout = (PinyinLayoutMode)reading.PinyinLayout,
            MessageTextScale = (double)(reading.MessageTextScale ?? 100) / 100,
            MessageColumns = settings.MessageColumns,
            MessageRetentionCount = (int)retentionCount,
            ShowUserAvatars = reading.ShowUserAvatars,
            ShowFanMedals = reading.ShowFanMedals,
            CompactDanmuDisplay = reading.CompactDisplay,
            CompactDanmuWrap = reading.CompactDanmuWrap,
            CompactSuperChatWrap = reading.CompactSuperChatWrap,
            AutoScrollLongMessages = reading.AutoScrollLongMessages,
            HideEmoticonDanmu = reading.HideEmoticonDanmu,
            DanmuRegexFilter = danmuRegexSettings,
            SpecialAttention = SpecialAttentionEditor.Model.CreateSnapshot(settings.SpecialAttention)
        };
        return true;
    }

    private bool TryBeginOperation()
    {
        if (Model.IsOperating)
        {
            return false;
        }
        Model.IsOperating = true;
        return true;
    }

    private void EndOperation()
    {
        Model.IsOperating = false;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SettingsDialogViewModel.SelectedSection))
        {
            surface.PageContent = pages[Model.SelectedSection];
        }
        else if (args.PropertyName == nameof(SettingsDialogViewModel.CheckForUpdatesOnStartup))
        {
            var startupEnabled = Model.CheckForUpdatesOnStartup;
            runtime.Configuration.Settings.CheckForUpdatesOnStartup = startupEnabled;
            StartupUpdatePreferenceChanged?.Invoke(startupEnabled);
            PendingUpdatePreferenceSave = SavePreferenceAsync();
        }
    }

    private void OnAppearanceChanged(object? sender, PropertyChangedEventArgs args)
    {
        var settings = runtime.Configuration.Settings;
        var appearance = Model.Appearance;
        if (args.PropertyName == nameof(SettingsAppearanceViewModel.LanguageIndex) &&
            appearance.LanguageIndex >= 0 && appearance.LanguageIndex < SettingsAppearanceViewModel.languageIds.Length)
        {
            settings.Language = SettingsAppearanceViewModel.languageIds[appearance.LanguageIndex];
            Localization.SetLanguage(settings.Language);
        }
        else if (args.PropertyName == nameof(SettingsAppearanceViewModel.ThemeIndex) &&
            appearance.ThemeIndex >= 0 && appearance.ThemeIndex < SettingsAppearanceViewModel.themeIds.Length &&
            settings.Theme != SettingsAppearanceViewModel.themeIds[appearance.ThemeIndex])
        {
            settings.Theme = SettingsAppearanceViewModel.themeIds[appearance.ThemeIndex];
            settings.ColorScheme.SelectedSchemeId = null;
            ApplyAppearance();
        }
        else if (args.PropertyName == nameof(SettingsAppearanceViewModel.UseWindowMenuOnMac))
        {
            settings.UseWindowMenuOnMac = appearance.UseWindowMenuOnMac;
            WindowMenuOnMacChanged?.Invoke(settings.UseWindowMenuOnMac);
            PendingMenuPreferenceSave = SavePreferenceAsync();
        }
    }

    private void ApplyAppearance()
    {
        var settings = runtime.Configuration.Settings;
        var application = Application.Current!;
        application.RequestedThemeVariant = settings.Theme switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
        runtime.Colors.Apply(application, settings.ColorScheme);
        colorSchemeEditor.RefreshDefaults();
        Model.Appearance.ThemeIndex = Math.Max(0, Array.IndexOf(SettingsAppearanceViewModel.themeIds, settings.Theme));
        runtime.NotifyAppearanceChanged();
    }

    private async Task SavePreferenceAsync()
    {
        try
        {
            await runtime.ConfigurationStore.SaveAsync(runtime.Configuration);
        }
        catch (Exception exception)
        {
            SetStatus(Localization.Get("Settings.SaveFailed") + " · " + exception.GetType().Name);
        }
    }

    private async Task ClearCacheAsync()
    {
        try
        {
            await runtime.ClearTranslationCacheAsync();
            if (Model.Data.RefreshCacheSizeCommand.ExecutionTask is { IsCompleted: false } pendingRefresh)
            {
                await pendingRefresh;
            }
            await Model.Data.RefreshCacheSizeCommand.ExecuteAsync(null);
            SetStatus(Localization.Get("Settings.CacheCleared"), isError: false);
        }
        catch (Exception exception)
        {
            SetStatus(Localization.Get("Settings.SaveFailed") + " · " + exception.GetType().Name);
        }
    }

    private async Task OpenDataDirectoryAsync()
    {
        try
        {
            if (!await openDataDirectory(runtime.ConfigurationStore.DirectoryPath))
            {
                SetStatus(Localization.Get("Settings.OpenDataDirectoryFailed"));
            }
        }
        catch (Exception exception)
        {
            SetStatus(Localization.Get("Settings.OpenDataDirectoryFailed") + " · " + exception.GetType().Name);
        }
    }

    private async Task<bool> LaunchDataDirectoryAsync(string path)
    {
        var owner = TopLevel.GetTopLevel(this)!;
        using var folder = await owner.StorageProvider.TryGetFolderFromPathAsync(path);
        return folder is not null && await owner.Launcher.LaunchFileAsync(folder);
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs args)
    {
        Localization.LanguageChanged += OnLanguageChanged;
        Model.RefreshLocalization();
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs args)
    {
        Localization.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        Model.RefreshLocalization();
    }

    private void SetStatus(string text, bool isError = true)
    {
        Model.Status = text;
        Model.IsStatusSuccess = !isError;
    }
}
