using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Controls.Primitives;
using Avalonia.Automation;
using Lucide.Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Appearance;
using Yohuke.DanmuPanel.Services.Messages;
using Yohuke.DanmuPanel.Services.Settings;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.Services.Updates;

namespace Yohuke.DanmuPanel.Views.Settings;

/// <summary>阅读台的外观、阅读、翻译、账号、数据和更新设置。</summary>
public sealed class SettingsView : UserControl
{
    private readonly SettingsDialogSurface surface = new();
    private readonly ColorSchemeEditor colorSchemeEditor;
    private readonly PanelRuntime runtime;
    private readonly SettingsShareService sharing = new();
    private readonly ISettingsTransferDialogs transferDialogs;
    private readonly Control accountsView;
    private readonly TextBlock status;
    private readonly List<Button> sectionButtons = [];
    private readonly CheckBox enabled;
    private readonly ComboBox provider;
    private readonly TextBox baseUrl;
    private readonly TextBox apiKey;
    private readonly TextBox model;
    private readonly ComboBox targetLanguage;
    private readonly TextBox extraPrompt;
    private readonly ComboBox displayMode;
    private readonly ComboBox backlogMode;
    private readonly NumericUpDown giftMinimum;
    private readonly ComboBox giftResetPolicy;
    private readonly CheckBox userPinyin;
    private readonly CheckBox giftPinyin;
    private readonly CheckBox danmuPinyin;
    private readonly CheckBox superChatPinyin;
    private readonly ComboBox pinyinLayout;
    private readonly NumericUpDown messageTextScale;
    private readonly CheckBox compactDisplay;
    private readonly CheckBox compactDanmuWrap;
    private readonly CheckBox compactSuperChatWrap;
    private readonly CheckBox autoScrollLongMessages;
    private readonly CheckBox hideEmoticonDanmu;
    private readonly CheckBox danmuRegexEnabled;
    private readonly TextBox danmuRegexPatterns;
    private readonly CheckBox windowMenuOnMac;
    private readonly ObservableCollection<GlossaryRow> glossary;
    private readonly TextBlock glossaryLanguage;
    private readonly DataGrid glossaryGrid;
    private readonly Button save;
    private bool operating;

    internal Task PendingMenuPreferenceSave { get; private set; } = Task.CompletedTask;
    internal Task PendingUpdatePreferenceSave { get; private set; } = Task.CompletedTask;
    internal Task PendingSettingsTransfer { get; private set; } = Task.CompletedTask;

    private string SelectedBaseUrl => ((TranslationProviderOption)provider.SelectedItem!).BaseUrl ?? baseUrl.Text?.Trim() ?? "";
    private string SelectedTargetLanguage => ((TranslationLanguageOption)targetLanguage.SelectedItem!).Code;

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
        ISettingsTransferDialogs? transferDialogs = null)
    {
        Content = surface;
        status = surface.FindControl<TextBlock>("SaveStatus")!;
        this.runtime = runtime;
        this.transferDialogs = transferDialogs ?? new SettingsTransferDialogs();
        this.accountsView = accountsView;
        var settings = runtime.Configuration.Settings;
        colorSchemeEditor = new(settings, runtime.Colors);
        windowMenuOnMac = Check("Settings.Appearance.WindowMenuOnMac", settings.UseWindowMenuOnMac);
        windowMenuOnMac.Name = "UseWindowMenuOnMacCheckBox";
        windowMenuOnMac.IsVisible = supportsNativeMenus;
        windowMenuOnMac.IsCheckedChanged += (_, _) =>
        {
            settings.UseWindowMenuOnMac = windowMenuOnMac.IsChecked == true;
            WindowMenuOnMacChanged?.Invoke(settings.UseWindowMenuOnMac);
            PendingMenuPreferenceSave = SavePreferenceAsync();
        };
        var translation = TranslationRequestFactory.Snapshot(settings.Translation);
        enabled = Check("Settings.TranslationEnabled", translation.Enabled);
        baseUrl = new() { Name = "TranslationCustomBaseUrl", Text = translation.BaseUrl, Watermark = "https://api.example.com/v1" };
        provider = CreateProviderSelector(translation.BaseUrl);
        apiKey = new() { Text = translation.ApiKey, PasswordChar = '●' };
        model = new() { Text = translation.Model };
        targetLanguage = CreateTargetLanguageSelector(translation.TargetLanguage);
        extraPrompt = new() { Text = translation.ExtraSystemPrompt, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 100 };
        displayMode = Choices(["Settings.Bilingual", "Settings.TranslationOnly"], (int)translation.DisplayMode);
        backlogMode = Choices(["Settings.Realtime", "Settings.AllMessages"], (int)translation.BacklogMode);
        giftMinimum = new() { Value = settings.Reading.GiftAutoPinMinimum, Minimum = 0, Maximum = 1000000000, Increment = 1, FormatString = "0.##" };
        giftResetPolicy = Choices(["Settings.ComboOnce", "Settings.ComboNewQuantity", "Settings.ComboNewValue"], (int)settings.Reading.GiftReadResetPolicy);
        userPinyin = Check("Settings.UserPinyin", settings.ShowUserPinyin);
        giftPinyin = Check("Settings.GiftPinyin", settings.ShowGiftPinyin);
        danmuPinyin = Check("Settings.DanmuPinyin", settings.ShowDanmuPinyin);
        danmuPinyin.Name = "ShowDanmuPinyinCheckBox";
        superChatPinyin = Check("Settings.SuperChatPinyin", settings.ShowSuperChatPinyin);
        superChatPinyin.Name = "ShowSuperChatPinyinCheckBox";
        pinyinLayout = Choices(["Settings.PinyinRuby", "Settings.PinyinSeparateLine"], (int)settings.PinyinLayout);
        pinyinLayout.Name = "PinyinLayoutComboBox";
        messageTextScale = new()
        {
            Name = "MessageTextScaleInput",
            Minimum = 50,
            Maximum = 200,
            Increment = 10,
            Value = (decimal)settings.MessageTextScale * 100,
            FormatString = "0"
        };
        compactDisplay = Check("Settings.CompactDisplay", settings.CompactDanmuDisplay);
        compactDisplay.Name = "CompactDanmuDisplayCheckBox";
        compactDanmuWrap = Check("Settings.CompactMessageWrap", settings.CompactDanmuWrap);
        compactDanmuWrap.Name = "CompactDanmuWrapCheckBox";
        compactSuperChatWrap = Check("Settings.CompactSuperChatWrap", settings.CompactSuperChatWrap);
        compactSuperChatWrap.Name = "CompactSuperChatWrapCheckBox";
        autoScrollLongMessages = Check("Settings.AutoScrollLongMessages", settings.AutoScrollLongMessages);
        autoScrollLongMessages.Name = "AutoScrollLongMessagesCheckBox";
        hideEmoticonDanmu = Check("Settings.HideEmoticonDanmu", settings.HideEmoticonDanmu);
        hideEmoticonDanmu.Name = "HideEmoticonDanmuCheckBox";
        danmuRegexEnabled = Check("Settings.DanmuRegexEnabled", settings.DanmuRegexFilter.Enabled);
        danmuRegexEnabled.Name = "DanmuRegexFilterEnabledCheckBox";
        danmuRegexPatterns = new()
        {
            Name = "DanmuRegexPatternsInput",
            Text = string.Join(Environment.NewLine, settings.DanmuRegexFilter.Patterns),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            MinHeight = 140,
            MaxHeight = 260
        };
        compactDanmuWrap.Bind(IsEnabledProperty, new Binding(nameof(CheckBox.IsChecked)) { Source = compactDisplay });
        compactSuperChatWrap.Bind(IsEnabledProperty, new Binding(nameof(CheckBox.IsChecked)) { Source = compactDisplay });
        autoScrollLongMessages.Bind(IsEnabledProperty, new Binding(nameof(CheckBox.IsChecked)) { Source = compactDisplay });
        glossary = new(translation.Glossary.GroupBy(entry => entry.Source.Trim(), StringComparer.Ordinal)
            .Select(entries => new GlossaryRow(entries.Key, SelectedTargetLanguage, entries)));
        glossaryLanguage = new() { Text = $"({SelectedTargetLanguage})", Opacity = 0.7 };
        glossaryGrid = new()
        {
            ItemsSource = glossary,
            AutoGenerateColumns = false,
            CanUserResizeColumns = true,
            MinHeight = 180,
            MaxHeight = 260,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            Columns =
            {
                new DataGridTextColumn { Header = Label("Settings.TermSource"), Binding = new Binding(nameof(GlossaryRow.Source)) { Mode = BindingMode.TwoWay }, Width = new(1, DataGridLengthUnitType.Star) },
                new DataGridTextColumn
                {
                    Header = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                        Children = { Label("Settings.TermTarget"), glossaryLanguage }
                    },
                    Binding = new Binding(nameof(GlossaryRow.Target)) { Mode = BindingMode.TwoWay },
                    Width = new(1, DataGridLengthUnitType.Star)
                }
            }
        };
        targetLanguage.SelectionChanged += (_, _) => SelectGlossaryLanguage();
        AddSection("Settings.Appearance", "Settings.AppearanceDescription", BuildAppearance());
        AddSection("Settings.Reading", "Settings.ReadingDescription", Stack(
            Card("Settings.MessageDisplay",
                WithHint(Row("Settings.MessageTextScale", messageTextScale), "Settings.MessageTextScaleHelp"),
                WithHint(hideEmoticonDanmu, "Settings.HideEmoticonDanmuHelp"), compactDisplay,
                new StackPanel
                {
                    Margin = new Thickness(24, 0, 0, 0), Spacing = 8,
                    Children =
                    {
                        compactDanmuWrap, compactSuperChatWrap,
                        WithHint(autoScrollLongMessages, "Settings.AutoScrollLongMessagesHelp")
                    }
                }),
            Card("Settings.DanmuRegexFilter",
                Row("Settings.DanmuRegexPatterns", danmuRegexPatterns)),
            Card("Settings.Reading", Row("Settings.GiftMinimum", giftMinimum), Row("Settings.ComboPolicy", giftResetPolicy)),
            Card("Settings.Pinyin",
                WithHint(userPinyin, "Settings.PinyinHelp"),
                WithHint(giftPinyin, "Settings.PinyinHelp"),
                WithHint(danmuPinyin, "Settings.PinyinHelp"),
                WithHint(superChatPinyin, "Settings.PinyinHelp"),
                WithHint(Row("Settings.PinyinLayout", pinyinLayout), "Settings.PinyinHelp", "Settings.PinyinScrollHelp"))));
        AddSection("Settings.Translation", "Settings.TranslationDescription", BuildTranslation());
        AddSection("Settings.Accounts", "Settings.AccountsDescription", accountsView);
        AddSection("Settings.Data", "Settings.DataDescription", BuildData());
        AddSection("Settings.Updates", "Settings.UpdatesDescription", BuildUpdates());
        save = surface.FindControl<Button>("SaveSettingsButton")!;
        save.Click += async (_, _) => await SaveAsync();

    }

    /// <summary>校验并保存设置；失败保留窗口及输入内容。</summary>
    public async Task<bool> SaveAsync()
    {
        if (!TryBeginOperation())
        {
            return false;
        }
        try
        {
            await Task.WhenAll(PendingMenuPreferenceSave, PendingUpdatePreferenceSave);
            if (!TryCreateDraft(out var draft))
            {
                return false;
            }
            sharing.ApplyTo(runtime.Configuration.Settings, draft);
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
        var regexLines = (danmuRegexPatterns.Text ?? "").Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
        var patterns = new List<string>();
        for (var index = 0; index < regexLines.Length; index++)
        {
            var pattern = regexLines[index];
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }
            if (!DanmuRegexFilter.IsValidPattern(pattern))
            {
                SetStatus(Localization.Format("Settings.DanmuRegexInvalidPattern", index + 1));
                return false;
            }
            patterns.Add(pattern);
        }
        if (!colorSchemeEditor.TryCommit(out var colorError))
        {
            SetStatus(colorError ?? Localization.Get("Colors.InvalidColor"));
            return false;
        }
        CommitGlossaryEdits();
        var terms = glossary.Where(entry => !string.IsNullOrWhiteSpace(entry.Source) || entry.HasTranslations).ToList();
        if (terms.Any(entry => string.IsNullOrWhiteSpace(entry.Source) || !entry.HasTranslations))
        {
            SetStatus(Localization.Get("Settings.IncompleteTerm"));
            return false;
        }
        if (enabled.IsChecked == true && (!Uri.TryCreate(SelectedBaseUrl, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment) || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            string.IsNullOrWhiteSpace(model.Text) || string.IsNullOrWhiteSpace(SelectedTargetLanguage)))
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
            UseWindowMenuOnMac = settings.UseWindowMenuOnMac,
            CheckForUpdatesOnStartup = settings.CheckForUpdatesOnStartup,
            Translation = new()
            {
                Enabled = enabled.IsChecked == true,
                BaseUrl = SelectedBaseUrl,
                ApiKey = apiKey.Text ?? "",
                Model = model.Text?.Trim() ?? "",
                TargetLanguage = SelectedTargetLanguage,
                ExtraSystemPrompt = extraPrompt.Text ?? "",
                DisplayMode = (TranslationDisplayMode)displayMode.SelectedIndex,
                BacklogMode = (TranslationBacklogMode)backlogMode.SelectedIndex,
                Glossary = terms.SelectMany(entry => entry.GetEntries()).ToList()
            },
            Reading = new()
            {
                GiftAutoPinMinimum = giftMinimum.Value ?? 0,
                GiftReadResetPolicy = (GiftReadResetPolicy)giftResetPolicy.SelectedIndex
            },
            ShowUserPinyin = userPinyin.IsChecked == true,
            ShowGiftPinyin = giftPinyin.IsChecked == true,
            ShowDanmuPinyin = danmuPinyin.IsChecked == true,
            ShowSuperChatPinyin = superChatPinyin.IsChecked == true,
            PinyinLayout = (PinyinLayoutMode)pinyinLayout.SelectedIndex,
            MessageTextScale = (double)(messageTextScale.Value ?? 100) / 100,
            MessageColumns = settings.MessageColumns,
            CompactDanmuDisplay = compactDisplay.IsChecked == true,
            CompactDanmuWrap = compactDanmuWrap.IsChecked == true,
            CompactSuperChatWrap = compactSuperChatWrap.IsChecked == true,
            AutoScrollLongMessages = autoScrollLongMessages.IsChecked == true,
            HideEmoticonDanmu = hideEmoticonDanmu.IsChecked == true,
            DanmuRegexFilter = new() { Enabled = danmuRegexEnabled.IsChecked == true, Patterns = patterns }
        };
        return true;
    }

    private bool TryBeginOperation()
    {
        if (operating)
        {
            return false;
        }
        operating = true;
        surface.IsEnabled = false;
        return true;
    }

    private void EndOperation()
    {
        operating = false;
        surface.IsEnabled = true;
    }

    private Control BuildAppearance()
    {
        var settings = runtime.Configuration.Settings;
        var languageIds = new[] { "system", "zh-CN", "en-US", "ja-JP" };
        var languages = Choices(["Settings.FollowSystem", "Settings.Chinese", "Settings.English", "Settings.Japanese"], Math.Max(0, Array.IndexOf(languageIds, settings.Language)));
        languages.SelectionChanged += (_, _) =>
        {
            if (languages.SelectedIndex >= 0)
            {
                settings.Language = languageIds[languages.SelectedIndex];
                Localization.SetLanguage(settings.Language);
            }
        };
        var themes = new[] { "system", "light", "dark" };
        var theme = Choices(["Settings.FollowSystem", "Settings.Light", "Settings.Dark"], Math.Max(0, Array.IndexOf(themes, settings.Theme)));
        void ApplyAppearance()
        {
            var application = Application.Current!;
            application.RequestedThemeVariant = settings.Theme switch
            {
                "light" => ThemeVariant.Light,
                "dark" => ThemeVariant.Dark,
                _ => ThemeVariant.Default
            };
            runtime.Colors.Apply(application, settings.ColorScheme);
            colorSchemeEditor.RefreshDefaults();
            theme.SelectedIndex = Math.Max(0, Array.IndexOf(themes, settings.Theme));
            runtime.NotifyAppearanceChanged();
        }
        colorSchemeEditor.Changed += ApplyAppearance;
        theme.SelectionChanged += (_, _) =>
        {
            if (theme.SelectedIndex >= 0 && settings.Theme != themes[theme.SelectedIndex])
            {
                settings.Theme = themes[theme.SelectedIndex];
                settings.ColorScheme.SelectedSchemeId = null;
                ApplyAppearance();
            }
        };
        return Stack(Card("Settings.Appearance", Row("Settings.Language", languages), Row("Settings.Theme", theme), windowMenuOnMac),
            colorSchemeEditor);
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

    private Control BuildTranslation()
    {
        var add = Ui.IconButton("Settings.AddTerm", LucideIconKind.Plus, () =>
        {
            AddTerm();
            return Task.CompletedTask;
        }, Report);
        var remove = Ui.IconButton("Settings.RemoveTerm", LucideIconKind.Trash2, () =>
        {
            RemoveTerm(glossaryGrid.SelectedItem as GlossaryRow);
            return Task.CompletedTask;
        }, Report);
        add.Name = "AddGlossaryTerm";
        remove.Name = "RemoveGlossaryTerm";
        remove.IsEnabled = false;
        glossaryGrid.SelectionChanged += (_, _) => remove.IsEnabled = glossaryGrid.SelectedItem is GlossaryRow;
        glossaryGrid.LoadingRow += (_, args) =>
        {
            var row = args.Row;
            var menu = new LocalizedContextMenu();
            var delete = new MenuItem { Icon = new LucideIcon { Kind = LucideIconKind.Trash2 } };
            menu.SetHeader(delete, "Panel.Delete");
            delete.Click += (_, _) => RemoveTerm(row.DataContext as GlossaryRow);
            menu.Items.Add(delete);
            row.ContextMenu = menu;
        };
        var tableHeader = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8 };
        var title = Label("Settings.Glossary");
        title.Classes.Add("sectionTitle");
        tableHeader.Children.Add(title);
        Grid.SetColumn(add, 1);
        Grid.SetColumn(remove, 2);
        tableHeader.Children.Add(add);
        tableHeader.Children.Add(remove);
        return Stack(
            Card("Settings.Translation", enabled,
                WithHint(Row("Settings.BaseUrl", Stack(provider, baseUrl)), "Settings.BaseUrlHelp"),
                Row("Settings.ApiKey", apiKey), Row("Settings.Model", model), Row("Settings.TargetLanguage", targetLanguage)),
            Card("Settings.DisplayMode", Row("Settings.DisplayMode", displayMode),
                WithHint(Row("Settings.Backlog", backlogMode), "Settings.BacklogHelp")),
            Card("Settings.ExtraPrompt", extraPrompt),
            Panel(Stack(tableHeader, glossaryGrid)));
    }

    private void AddTerm()
    {
        CommitGlossaryEdits();
        var term = new GlossaryRow("", SelectedTargetLanguage, []);
        glossary.Add(term);
        glossaryGrid.SelectedItem = term;
        glossaryGrid.ScrollIntoView(term, glossaryGrid.Columns[0]);
    }

    private void RemoveTerm(GlossaryRow? term)
    {
        if (term is not null)
        {
            CommitGlossaryEdits();
            glossary.Remove(term);
        }
    }

    private void SelectGlossaryLanguage()
    {
        CommitGlossaryEdits();
        foreach (var term in glossary)
        {
            term.SelectLanguage(SelectedTargetLanguage);
        }
        glossaryLanguage.Text = $"({SelectedTargetLanguage})";
    }

    private void CommitGlossaryEdits()
    {
        glossaryGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        glossaryGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    private Control BuildData()
    {
        var import = ActionButton("Settings.Import", LucideIconKind.Import);
        import.Name = "ImportSettingsButton";
        import.Click += (_, _) => PendingSettingsTransfer = ImportSettingsAsync();
        var export = ActionButton("Settings.Export", LucideIconKind.Upload);
        export.Name = "ExportSettingsButton";
        export.Click += (_, _) => PendingSettingsTransfer = ExportSettingsAsync();
        var clearCurrent = ActionButton("Settings.ClearCurrent", LucideIconKind.Trash2);
        clearCurrent.Click += (_, _) => ClearRequested?.Invoke(false);
        var clearAll = ActionButton("Settings.ClearAll", LucideIconKind.Trash2);
        clearAll.Click += (_, _) => ClearRequested?.Invoke(true);
        var cache = ActionButton("Settings.ClearCache", LucideIconKind.Database);
        cache.Click += async (_, _) =>
        {
            cache.IsEnabled = false;
            try
            {
                await runtime.ClearTranslationCacheAsync();
                SetStatus(Localization.Get("Settings.CacheCleared"), isError: false);
            }
            catch (Exception exception)
            {
                SetStatus(Localization.Get("Settings.SaveFailed") + " · " + exception.GetType().Name);
            }
            finally
            {
                cache.IsEnabled = true;
            }
        };
        return Stack(Card("Settings.Transfer",
                WithHint(new WrapPanel
                {
                    Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8,
                    Children = { import, export }
                }, "Settings.TransferHelp", "Settings.TransferKeyHelp")),
            Card("Settings.Data",
                WithHint(clearCurrent, "Settings.HistoryHelp"), WithHint(clearAll, "Settings.HistoryHelp")),
            Card("Settings.ClearCache", WithHint(cache, "Settings.CacheHelp")));
    }

    private Control BuildUpdates()
    {
        var startup = Check("Settings.CheckForUpdatesOnStartup", runtime.Configuration.Settings.CheckForUpdatesOnStartup);
        startup.Name = "CheckForUpdatesOnStartupCheckBox";
        startup.IsCheckedChanged += (_, _) =>
        {
            var startupEnabled = startup.IsChecked == true;
            runtime.Configuration.Settings.CheckForUpdatesOnStartup = startupEnabled;
            StartupUpdatePreferenceChanged?.Invoke(startupEnabled);
            PendingUpdatePreferenceSave = SavePreferenceAsync();
        };
        var check = ActionButton("Settings.CheckForUpdates", LucideIconKind.RefreshCw);
        check.Name = "CheckForUpdatesButton";
        check.Click += (_, _) => CheckForUpdatesRequested?.Invoke();
        return Stack(Card("Settings.Updates",
            Row("Updates.CurrentVersion", new TextBlock
            {
                Name = "CurrentAppVersion",
                Text = AppUpdateVersion.Current.ToString(),
                VerticalAlignment = VerticalAlignment.Center
            }),
            WithHint(startup, "Settings.UpdatesHelp"), WithHint(check, "Settings.UpdatesHelp")));
    }

    private void AddSection(string key, string descriptionKey, Control content)
    {
        var title = Label(key);
        title.FontWeight = FontWeight.SemiBold;
        var description = Help(descriptionKey);
        var button = new Button { Content = new StackPanel { Spacing = 2, Children = { title, description } } };
        button.Name = key.Replace("Settings.", "SettingsSection", StringComparison.Ordinal);
        button.Classes.Add("settingsSectionButton");
        button.Bind(AutomationProperties.NameProperty, Localization.Observe(key));
        var page = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = content
        };
        button.Click += (_, _) => SelectSection(button, page, descriptionKey);
        sectionButtons.Add(button);
        surface.FindControl<StackPanel>("SectionNavigation")!.Children.Add(button);
        if (sectionButtons.Count == 1)
        {
            SelectSection(button, page, descriptionKey);
        }
    }

    private void SelectSection(Button selected, Control page, string descriptionKey)
    {
        foreach (var button in sectionButtons)
        {
            button.Classes.Set("selected", button == selected);
        }
        surface.FindControl<ContentControl>("SectionContent")!.Content = page;
        surface.FindControl<TextBlock>("SectionDescription")!.Bind(TextBlock.TextProperty, Localization.Observe(descriptionKey));
    }

    private void SetStatus(string text, bool isError = true)
    {
        status.Text = text;
        status.Classes.Set("success", !isError);
        status.IsVisible = !string.IsNullOrEmpty(text);
    }

    private void Report(Exception exception)
    {
        SetStatus(Localization.Get("Settings.SaveFailed") + " · " + exception.GetType().Name);
    }

    private static Border Card(string key, params Control[] controls)
    {
        var title = Label(key);
        title.Classes.Add("sectionTitle");
        return Panel(Stack([title, .. controls]));
    }

    private static Border Panel(Control content)
    {
        var panel = new Border { Padding = new Thickness(14), Child = content };
        panel.Classes.Add("panel");
        return panel;
    }

    private static StackPanel Stack(params Control[] controls)
    {
        var panel = new StackPanel { Spacing = 14 };
        foreach (var control in controls)
        {
            panel.Children.Add(control);
        }
        return panel;
    }

    private static Grid Row(string key, Control content)
    {
        var row = new Grid { ColumnDefinitions = new("180,*"), ColumnSpacing = 14 };
        var label = Label(key);
        label.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(label);
        Grid.SetColumn(content, 1);
        row.Children.Add(content);
        content.HorizontalAlignment = HorizontalAlignment.Stretch;
        return row;
    }

    private static Border WithHint(Control content, params string[] keys)
    {
        var tooltip = new StackPanel { MaxWidth = 480, Spacing = 8 };
        foreach (var key in keys)
        {
            tooltip.Children.Add(Label(key));
        }
        var row = new Border { Background = Brushes.Transparent, Child = content };
        ToolTip.SetTip(row, tooltip);
        return row;
    }

    private static TextBlock Label(string key)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap };
        block.Bind(TextBlock.TextProperty, Localization.Observe(key));
        return block;
    }

    private static TextBlock Help(string key)
    {
        var block = Label(key);
        block.Opacity = 0.7;
        block.FontSize = 12;
        return block;
    }

    private static Button ActionButton(string key, LucideIconKind icon)
    {
        return new()
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { new LucideIcon { Kind = icon }, Label(key) }
            }
        };
    }

    private static CheckBox Check(string key, bool value)
    {
        var check = new CheckBox { IsChecked = value };
        check.Bind(ContentControl.ContentProperty, Localization.Observe(key));
        return check;
    }

    private ComboBox CreateProviderSelector(string currentUrl)
    {
        var presets = TranslationProviderOption.Presets;
        var selected = presets.FirstOrDefault(option =>
            string.Equals(option.BaseUrl, currentUrl.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) ?? presets[^1];
        var selector = new ComboBox
        {
            Name = "TranslationProvider",
            ItemsSource = presets,
            SelectedItem = selected,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = new FuncDataTemplate<TranslationProviderOption>((option, _) =>
                option?.BaseUrl is null ? Label("Settings.CustomProvider") : new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock { Text = option.Name },
                        new TextBlock { Text = option.BaseUrl, FontSize = 11, Opacity = 0.7 }
                    }
                })
        };
        baseUrl.IsVisible = selected.BaseUrl is null;
        selector.SelectionChanged += (_, _) =>
            baseUrl.IsVisible = selector.SelectedItem is TranslationProviderOption { BaseUrl: null };
        return selector;
    }

    private static ComboBox CreateTargetLanguageSelector(string currentLanguage)
    {
        var options = TranslationLanguageOption.Presets.ToList();
        var selected = options.FirstOrDefault(option => option.Code == currentLanguage);
        if (selected is null)
        {
            selected = new(currentLanguage);
            options.Add(selected);
        }
        return new()
        {
            Name = "TranslationTargetLanguage",
            ItemsSource = options,
            SelectedItem = selected,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = new FuncDataTemplate<TranslationLanguageOption>((option, _) =>
            {
                if (option is null)
                {
                    return new TextBlock();
                }
                var name = option.NameKey is { } key ? Label(key) : new TextBlock { Text = option.NativeName };
                return new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children = { name, new TextBlock { Text = $"({option.Code})" } }
                };
            })
        };
    }

    private static ComboBox Choices(string[] keys, int selectedIndex)
    {
        return new()
        {
            ItemsSource = keys,
            ItemTemplate = new FuncDataTemplate<string>((key, _) => key is null ? new TextBlock() : Label(key)),
            SelectedIndex = Math.Clamp(selectedIndex, 0, keys.Length - 1),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
    }
}
