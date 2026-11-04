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
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Translation;

namespace Yohuke.DanmuPanel.Views.Settings;

/// <summary>阅读台的外观、阅读、翻译、账号和数据设置。</summary>
public sealed class SettingsView : UserControl
{
    private readonly SettingsDialogSurface surface = new();
    private readonly PanelRuntime runtime;
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
    private readonly CheckBox compactDisplay;
    private readonly CheckBox compactDanmuWrap;
    private readonly CheckBox compactSuperChatWrap;
    private readonly CheckBox windowMenuOnMac;
    private readonly ObservableCollection<GlossaryEntry> glossary;
    private readonly DataGrid glossaryGrid;
    private readonly Button save;
    private bool saving;

    internal Task PendingMenuPreferenceSave { get; private set; } = Task.CompletedTask;

    private string SelectedBaseUrl => ((TranslationProviderOption)provider.SelectedItem!).BaseUrl ?? baseUrl.Text?.Trim() ?? "";
    private string SelectedTargetLanguage => ((TranslationLanguageOption)targetLanguage.SelectedItem!).Code;

    public event Action? Saved;
    public event Action<bool>? ClearRequested;

    /// <summary>通知应用即时切换 macOS 菜单位置。</summary>
    public event Action<bool>? WindowMenuOnMacChanged;

    /// <summary>创建设置面板，账号界面由应用提供。</summary>
    public SettingsView(PanelRuntime runtime, Control accountsView)
        : this(runtime, accountsView, OperatingSystem.IsMacOS())
    {
    }

    internal SettingsView(PanelRuntime runtime, Control accountsView, bool supportsNativeMenus)
    {
        Content = surface;
        status = surface.FindControl<TextBlock>("SaveStatus")!;
        this.runtime = runtime;
        this.accountsView = accountsView;
        var settings = runtime.Configuration.Settings;
        windowMenuOnMac = Check("Settings.Appearance.WindowMenuOnMac", settings.UseWindowMenuOnMac);
        windowMenuOnMac.Name = "UseWindowMenuOnMacCheckBox";
        windowMenuOnMac.IsVisible = supportsNativeMenus;
        windowMenuOnMac.IsCheckedChanged += (_, _) =>
        {
            settings.UseWindowMenuOnMac = windowMenuOnMac.IsChecked == true;
            WindowMenuOnMacChanged?.Invoke(settings.UseWindowMenuOnMac);
            PendingMenuPreferenceSave = SaveMenuPreferenceAsync();
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
        compactDisplay = Check("Settings.CompactDisplay", settings.CompactDanmuDisplay);
        compactDisplay.Name = "CompactDanmuDisplayCheckBox";
        compactDanmuWrap = Check("Settings.CompactMessageWrap", settings.CompactDanmuWrap);
        compactDanmuWrap.Name = "CompactDanmuWrapCheckBox";
        compactSuperChatWrap = Check("Settings.CompactSuperChatWrap", settings.CompactSuperChatWrap);
        compactSuperChatWrap.Name = "CompactSuperChatWrapCheckBox";
        compactDanmuWrap.Bind(IsEnabledProperty, new Binding(nameof(CheckBox.IsChecked)) { Source = compactDisplay });
        compactSuperChatWrap.Bind(IsEnabledProperty, new Binding(nameof(CheckBox.IsChecked)) { Source = compactDisplay });
        glossary = new(translation.Glossary);
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
                new DataGridTextColumn { Header = Label("Settings.TermSource"), Binding = new Binding(nameof(GlossaryEntry.Source)) { Mode = BindingMode.TwoWay }, Width = new(1, DataGridLengthUnitType.Star) },
                new DataGridTextColumn { Header = Label("Settings.TermTarget"), Binding = new Binding(nameof(GlossaryEntry.Target)) { Mode = BindingMode.TwoWay }, Width = new(1, DataGridLengthUnitType.Star) },
                new DataGridTextColumn { Header = Label("Settings.TermLanguage"), Binding = new Binding(nameof(GlossaryEntry.TargetLanguage)) { Mode = BindingMode.TwoWay }, Width = new(120) }
            }
        };
        AddSection("Settings.Appearance", "Settings.AppearanceDescription", BuildAppearance());
        AddSection("Settings.Reading", "Settings.ReadingDescription", Stack(
            Card("Settings.MessageDisplay", compactDisplay,
                new StackPanel { Margin = new Thickness(24, 0, 0, 0), Spacing = 8, Children = { compactDanmuWrap, compactSuperChatWrap } }),
            Card("Settings.Reading", Row("Settings.GiftMinimum", giftMinimum), Row("Settings.ComboPolicy", giftResetPolicy)),
            Card("Settings.UserPinyin", userPinyin, giftPinyin, Help("Settings.PinyinHelp"))));
        AddSection("Settings.Translation", "Settings.TranslationDescription", BuildTranslation());
        AddSection("Settings.Accounts", "Settings.AccountsDescription", accountsView);
        AddSection("Settings.Data", "Settings.DataDescription", BuildData());
        save = surface.FindControl<Button>("SaveSettingsButton")!;
        save.Click += async (_, _) => await SaveAsync();

    }

    /// <summary>校验并保存设置；失败保留窗口及输入内容。</summary>
    public async Task<bool> SaveAsync()
    {
        if (saving)
        {
            return false;
        }

        saving = true;
        save.IsEnabled = false;
        try
        {
            await PendingMenuPreferenceSave;
            glossaryGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            glossaryGrid.CommitEdit(DataGridEditingUnit.Row, true);
            var terms = glossary.Where(entry => !string.IsNullOrWhiteSpace(entry.Source) || !string.IsNullOrWhiteSpace(entry.Target)).ToList();
            if (terms.Any(entry => string.IsNullOrWhiteSpace(entry.Source) || string.IsNullOrWhiteSpace(entry.Target) || string.IsNullOrWhiteSpace(entry.TargetLanguage)))
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
            settings.Translation = new()
            {
                Enabled = enabled.IsChecked == true,
                BaseUrl = SelectedBaseUrl,
                ApiKey = apiKey.Text ?? "",
                Model = model.Text?.Trim() ?? "",
                TargetLanguage = SelectedTargetLanguage,
                ExtraSystemPrompt = extraPrompt.Text ?? "",
                DisplayMode = (TranslationDisplayMode)displayMode.SelectedIndex,
                BacklogMode = (TranslationBacklogMode)backlogMode.SelectedIndex,
                Glossary = terms.Select(entry => new GlossaryEntry
                {
                    Source = entry.Source.Trim(), Target = entry.Target.Trim(), TargetLanguage = entry.TargetLanguage.Trim()
                }).ToList()
            };
            settings.Reading = new()
            {
                GiftAutoPinMinimum = giftMinimum.Value ?? 0,
                GiftReadResetPolicy = (GiftReadResetPolicy)giftResetPolicy.SelectedIndex
            };
            settings.ShowUserPinyin = userPinyin.IsChecked == true;
            settings.ShowGiftPinyin = giftPinyin.IsChecked == true;
            settings.CompactDanmuDisplay = compactDisplay.IsChecked == true;
            settings.CompactDanmuWrap = compactDanmuWrap.IsChecked == true;
            settings.CompactSuperChatWrap = compactSuperChatWrap.IsChecked == true;
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
            saving = false;
            save.IsEnabled = true;
        }
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
        theme.SelectionChanged += (_, _) =>
        {
            if (theme.SelectedIndex >= 0)
            {
                settings.Theme = themes[theme.SelectedIndex];
                Application.Current!.RequestedThemeVariant = settings.Theme switch
                {
                    "light" => ThemeVariant.Light,
                    "dark" => ThemeVariant.Dark,
                    _ => ThemeVariant.Default
                };
            }
        };
        return Stack(Card("Settings.Appearance", Row("Settings.Language", languages), Row("Settings.Theme", theme), windowMenuOnMac));
    }

    private async Task SaveMenuPreferenceAsync()
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
            RemoveTerm(glossaryGrid.SelectedItem as GlossaryEntry);
            return Task.CompletedTask;
        }, Report);
        add.Name = "AddGlossaryTerm";
        remove.Name = "RemoveGlossaryTerm";
        remove.IsEnabled = false;
        glossaryGrid.SelectionChanged += (_, _) => remove.IsEnabled = glossaryGrid.SelectedItem is GlossaryEntry;
        glossaryGrid.LoadingRow += (_, args) =>
        {
            var row = args.Row;
            var menu = new LocalizedContextMenu();
            var delete = new MenuItem { Icon = new LucideIcon { Kind = LucideIconKind.Trash2 } };
            menu.SetHeader(delete, "Panel.Delete");
            delete.Click += (_, _) => RemoveTerm(row.DataContext as GlossaryEntry);
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
            Card("Settings.Translation", enabled, Row("Settings.BaseUrl", Stack(provider, baseUrl)), Help("Settings.BaseUrlHelp"),
                Row("Settings.ApiKey", apiKey), Row("Settings.Model", model), Row("Settings.TargetLanguage", targetLanguage)),
            Card("Settings.DisplayMode", Row("Settings.DisplayMode", displayMode), Row("Settings.Backlog", backlogMode), Help("Settings.BacklogHelp")),
            Card("Settings.ExtraPrompt", extraPrompt),
            Panel(Stack(tableHeader, glossaryGrid)));
    }

    private void AddTerm()
    {
        glossaryGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var term = new GlossaryEntry { TargetLanguage = SelectedTargetLanguage };
        glossary.Add(term);
        glossaryGrid.SelectedItem = term;
        glossaryGrid.ScrollIntoView(term, glossaryGrid.Columns[0]);
    }

    private void RemoveTerm(GlossaryEntry? term)
    {
        if (term is not null)
        {
            glossaryGrid.CommitEdit(DataGridEditingUnit.Row, true);
            glossary.Remove(term);
        }
    }

    private Control BuildData()
    {
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
                SetStatus(Localization.Get("Settings.CacheCleared"));
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
        return Stack(Card("Settings.Data", Help("Settings.HistoryHelp"), clearCurrent, clearAll),
            Card("Settings.ClearCache", Help("Settings.CacheHelp"), cache));
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
        content.Classes.Add("scrollSafeContent");
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

    private void SetStatus(string text)
    {
        status.Text = text;
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
