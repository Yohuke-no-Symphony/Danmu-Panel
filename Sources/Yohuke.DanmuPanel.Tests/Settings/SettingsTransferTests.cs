using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Settings;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Views.Settings;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Settings;

/// <summary>通过离线文件流验证设置迁移与现有设置草稿及窗口生命周期的交互。</summary>
public sealed class SettingsTransferTests
{
    /// <summary>导出按钮输出当前草稿和原始 Key，截断旧文件且不保存或关闭设置。</summary>
    [AvaloniaFact]
    public async Task ExportButtonWritesDraftAndTruncatesExistingFileWithoutSavingOrClosing()
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = CreateLocalConfiguration();
        var store = new ConfigurationService(directory.Path);
        await store.SaveAsync(configuration);
        await using var runtime = new PanelRuntime(store, configuration);
        var original = JsonSerializer.Serialize(configuration.Settings);
        var destination = new MemoryStream();
        destination.Write(new byte[16384]);
        destination.Position = 0;
        var dialogs = new ControlledSettingsTransferDialogs { ExportDestination = destination };
        var view = new SettingsView(runtime, new TextBlock(), true, dialogs);
        var window = new Window { Content = view, Width = 1020, Height = 760 };
        var saved = false;
        view.Saved += () => saved = true;
        try
        {
            Show(window);
            SelectSection(view, "Translation");
            FindModel(view, "original-model").Text = "draft-model";
            FindApiKey(view).Text = "  draft-key-测试  ";
            SelectSection(view, "Reading");
            Find<NumericUpDown>(view, "MessageTextScaleInput").Value = 150;
            Find<CheckBox>(view, "ShowDanmuPinyinCheckBox").IsChecked = true;
            SelectSection(view, "Data");
            CaptureDataPageIfRequested(window);

            Click(view, "ExportSettingsButton");
            await view.PendingSettingsTransfer;

            using var document = JsonDocument.Parse(destination.ToArray());
            var exported = document.RootElement.GetProperty("Settings");
            Assert.Equal("draft-model", exported.GetProperty("Translation").GetProperty("Model").GetString());
            Assert.Equal("  draft-key-测试  ", exported.GetProperty("Translation").GetProperty("ApiKey").GetString());
            Assert.Equal(1.5, exported.GetProperty("MessageTextScale").GetDouble());
            Assert.True(exported.GetProperty("ShowDanmuPinyin").GetBoolean());
            Assert.Equal(original, JsonSerializer.Serialize(configuration.Settings));
            Assert.Equal(original, JsonSerializer.Serialize(store.Load().Settings));
            Assert.False(saved);
            Assert.True(window.IsVisible);
            Assert.Same(window, dialogs.LastExportOwner);
            Assert.True(Assert.IsAssignableFrom<Control>(view.Content).IsEnabled);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>导入立即持久化并关闭真实设置窗口，旧草稿不能覆盖导入值。</summary>
    [AvaloniaFact]
    public async Task SuccessfulImportAppliesPreferencesAndClosesWithoutSavingOldDraft()
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = CreateLocalConfiguration();
        var store = new ConfigurationService(directory.Path);
        await store.SaveAsync(configuration);
        await using var runtime = new PanelRuntime(store, configuration);
        var originalSettings = configuration.Settings;
        var originalAccounts = configuration.Accounts;
        var originalWorkspaces = configuration.Workspaces;
        var imported = CreateImportedSettings();
        var dialogs = new ControlledSettingsTransferDialogs { ImportSource = CreateImportStream(imported) };
        var window = new SettingsWindow(runtime, new TextBlock(), dialogs);
        SettingsView? view = null;
        var originalLanguage = Localization.SelectedLanguageID;
        var originalTheme = Application.Current!.RequestedThemeVariant;
        bool? menuPreference = null;
        bool? updatePreference = null;
        var saved = 0;
        window.WindowMenuOnMacChanged += value => menuPreference = value;
        window.StartupUpdatePreferenceChanged += value => updatePreference = value;
        try
        {
            Show(window);
            view = window.GetVisualDescendants().OfType<SettingsView>().Single();
            view.Saved += () => saved++;
            SelectSection(view, "Translation");
            FindModel(view, "original-model").Text = "stale-draft";
            FindApiKey(view).Text = "stale-key";

            Assert.True(await view.ImportSettingsAsync());

            Assert.False(window.IsVisible);
            Assert.Equal(1, saved);
            Assert.Same(originalSettings, configuration.Settings);
            Assert.Same(originalAccounts, configuration.Accounts);
            Assert.Same(originalWorkspaces, configuration.Workspaces);
            Assert.Equal("local-account", configuration.Settings.DefaultAccountId);
            Assert.Equal("local-workspace", configuration.Settings.SelectedWorkspaceId);
            Assert.Equal(imported.Translation.Model, configuration.Settings.Translation.Model);
            Assert.Equal(imported.Translation.ApiKey, configuration.Settings.Translation.ApiKey);
            Assert.Equal(imported.Translation.Model, store.Load().Settings.Translation.Model);
            Assert.Equal(imported.Translation.ApiKey, store.Load().Settings.Translation.ApiKey);
            Assert.Equal("ja-JP", Localization.SelectedLanguageID);
            Assert.Equal(Avalonia.Styling.ThemeVariant.Dark, Application.Current.RequestedThemeVariant);
            Assert.True(Application.Current.Resources.TryGetResource("AppPrimaryBrush", Application.Current.ActualThemeVariant, out var brush));
            Assert.Equal(Color.Parse("#123456"), Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color);
            Assert.True(menuPreference);
            Assert.False(updatePreference);
            Assert.Same(window, dialogs.LastImportOwner);
        }
        finally
        {
            if (window.IsVisible && view is not null)
            {
                await view.SaveAsync();
            }
            window.Close();
            Localization.SetLanguage(originalLanguage);
            Application.Current.RequestedThemeVariant = originalTheme;
        }
    }

    /// <summary>取消选择、取消覆盖和无效文件均保留运行设置、磁盘配置及编辑草稿。</summary>
    [AvaloniaTheory]
    [InlineData("picker")]
    [InlineData("confirmation")]
    [InlineData("invalid")]
    public async Task AbortedImportPreservesConfigurationAndDraft(string reason)
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = CreateLocalConfiguration();
        var store = new ConfigurationService(directory.Path);
        await store.SaveAsync(configuration);
        await using var runtime = new PanelRuntime(store, configuration);
        var original = JsonSerializer.Serialize(configuration.Settings);
        var dialogs = new ControlledSettingsTransferDialogs
        {
            ImportSource = reason switch
            {
                "picker" => null,
                "invalid" => new MemoryStream(Encoding.UTF8.GetBytes("{invalid")),
                _ => CreateImportStream(CreateImportedSettings())
            },
            Confirmation = Task.FromResult(false)
        };
        var view = new SettingsView(runtime, new TextBlock(), true, dialogs);
        var window = new Window { Content = view, Width = 1020, Height = 760 };
        var saved = false;
        view.Saved += () => saved = true;
        try
        {
            Show(window);
            SelectSection(view, "Translation");
            var draft = FindModel(view, "original-model");
            draft.Text = "retained-draft";

            Assert.False(await view.ImportSettingsAsync());

            Assert.Equal(original, JsonSerializer.Serialize(configuration.Settings));
            Assert.Equal(original, JsonSerializer.Serialize(store.Load().Settings));
            Assert.Equal("retained-draft", draft.Text);
            Assert.False(saved);
            Assert.True(window.IsVisible);
            Assert.True(Assert.IsAssignableFrom<Control>(view.Content).IsEnabled);
            Assert.Equal(reason == "confirmation" ? 1 : 0, dialogs.ConfirmationRequests);
            if (reason == "invalid")
            {
                Assert.Contains(Localization.Get("Settings.InvalidSettingsFile"), Find<TextBlock>(view, "SaveStatus").Text);
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>等待覆盖确认期间禁止重复操作、保存和关闭，完成后只持久化导入值。</summary>
    [AvaloniaFact]
    public async Task PendingImportBlocksEditingSavingReentryAndWindowClose()
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = CreateLocalConfiguration();
        var store = new ConfigurationService(directory.Path);
        await store.SaveAsync(configuration);
        await using var runtime = new PanelRuntime(store, configuration);
        var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialogs = new ControlledSettingsTransferDialogs
        {
            ImportSource = CreateImportStream(CreateImportedSettings()),
            Confirmation = confirmation.Task
        };
        var window = new SettingsWindow(runtime, new TextBlock(), dialogs);
        SettingsView? view = null;
        var originalLanguage = Localization.SelectedLanguageID;
        var originalTheme = Application.Current!.RequestedThemeVariant;
        Task? import = null;
        try
        {
            Show(window);
            view = window.GetVisualDescendants().OfType<SettingsView>().Single();
            SelectSection(view, "Translation");
            FindModel(view, "original-model").Text = "must-not-save";
            SelectSection(view, "Data");
            Click(view, "ImportSettingsButton");
            import = view.PendingSettingsTransfer;
            await dialogs.ConfirmationOpened.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.False(Assert.IsAssignableFrom<Control>(view.Content).IsEnabled);
            Assert.False(await view.SaveAsync());
            Assert.False(await view.ImportSettingsAsync());
            Assert.False(await view.ExportSettingsAsync());
            window.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.IsVisible);
            Assert.Equal("original-model", store.Load().Settings.Translation.Model);
            Assert.Equal(1, dialogs.ImportRequests);
            Assert.Equal(0, dialogs.ExportRequests);

            confirmation.TrySetResult(true);
            await import;

            Assert.False(window.IsVisible);
            Assert.Equal("imported-model", store.Load().Settings.Translation.Model);
        }
        finally
        {
            confirmation.TrySetResult(false);
            if (import is not null)
            {
                await import;
            }
            if (window.IsVisible && view is not null)
            {
                await view.SaveAsync();
            }
            window.Close();
            Localization.SetLanguage(originalLanguage);
            Application.Current.RequestedThemeVariant = originalTheme;
        }
    }

    /// <summary>导出前验证翻译草稿，无效输入不打开文件选择器或修改当前配置。</summary>
    [AvaloniaFact]
    public async Task InvalidExportDraftDoesNotOpenFilePickerOrChangeSettings()
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = CreateLocalConfiguration();
        configuration.Settings.Translation.Enabled = true;
        await using var runtime = new PanelRuntime(new(directory.Path), configuration);
        var original = JsonSerializer.Serialize(configuration.Settings);
        using var destination = new MemoryStream();
        var dialogs = new ControlledSettingsTransferDialogs { ExportDestination = destination };
        var view = new SettingsView(runtime, new TextBlock(), true, dialogs);
        var window = new Window { Content = view, Width = 1020, Height = 760 };
        try
        {
            Show(window);
            SelectSection(view, "Translation");
            var model = FindModel(view, "original-model");
            model.Text = " ";

            Assert.False(await view.ExportSettingsAsync());

            Assert.Equal(0, dialogs.ExportRequests);
            Assert.Equal(original, JsonSerializer.Serialize(configuration.Settings));
            Assert.Contains(Localization.Get("Settings.InvalidTranslation"), Find<TextBlock>(view, "SaveStatus").Text);
            Assert.Equal(" ", model.Text);
            Assert.True(window.IsVisible);
            Assert.True(Assert.IsAssignableFrom<Control>(view.Content).IsEnabled);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>取消导出不写磁盘、发布设置或关闭窗口。</summary>
    [AvaloniaFact]
    public async Task CanceledExportLeavesSettingsAndWindowUntouched()
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = CreateLocalConfiguration();
        await using var runtime = new PanelRuntime(new(directory.Path), configuration);
        var original = JsonSerializer.Serialize(configuration.Settings);
        var dialogs = new ControlledSettingsTransferDialogs();
        var view = new SettingsView(runtime, new TextBlock(), true, dialogs);
        var window = new Window { Content = view, Width = 1020, Height = 760 };
        try
        {
            Show(window);

            Assert.False(await view.ExportSettingsAsync());

            Assert.Equal(original, JsonSerializer.Serialize(configuration.Settings));
            Assert.False(File.Exists(Path.Combine(directory.Path, "settings.json")));
            Assert.True(window.IsVisible);
            Assert.Equal(1, dialogs.ExportRequests);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>导出写入失败后恢复交互，用户可以立即选择另一文件重试。</summary>
    [AvaloniaFact]
    public async Task ExportWriteFailureAllowsRetryWithoutSavingDraft()
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = CreateLocalConfiguration();
        await using var runtime = new PanelRuntime(new(directory.Path), configuration);
        var original = JsonSerializer.Serialize(configuration.Settings);
        var dialogs = new ControlledSettingsTransferDialogs { ExportDestination = new MemoryStream(new byte[10], false) };
        var view = new SettingsView(runtime, new TextBlock(), true, dialogs);
        var window = new Window { Content = view, Width = 1020, Height = 760 };
        try
        {
            Show(window);
            Assert.False(await view.ExportSettingsAsync());
            Assert.True(Assert.IsAssignableFrom<Control>(view.Content).IsEnabled);
            Assert.True(window.IsVisible);
            var destination = new MemoryStream();
            dialogs.ExportDestination = destination;

            Assert.True(await view.ExportSettingsAsync());

            using var document = JsonDocument.Parse(destination.ToArray());
            Assert.Equal("original-model", document.RootElement.GetProperty("Settings").GetProperty("Translation").GetProperty("Model").GetString());
            Assert.Equal(2, dialogs.ExportRequests);
            Assert.Equal(original, JsonSerializer.Serialize(configuration.Settings));
            Assert.False(File.Exists(Path.Combine(directory.Path, "settings.json")));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>导入文件读取失败报告导入错误，并允许重新选择有效文件。</summary>
    [AvaloniaFact]
    public async Task ImportReadFailureAllowsRetry()
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = CreateLocalConfiguration();
        var store = new ConfigurationService(directory.Path);
        await store.SaveAsync(configuration);
        await using var runtime = new PanelRuntime(store, configuration);
        var original = JsonSerializer.Serialize(configuration.Settings);
        var dialogs = new ControlledSettingsTransferDialogs { ImportException = new IOException("Controlled read failure") };
        var view = new SettingsView(runtime, new TextBlock(), true, dialogs);
        var window = new Window { Content = view, Width = 1020, Height = 760 };
        var originalLanguage = Localization.SelectedLanguageID;
        var originalTheme = Application.Current!.RequestedThemeVariant;
        try
        {
            Show(window);
            Assert.False(await view.ImportSettingsAsync());
            Assert.Contains(Localization.Get("Settings.ImportFailed"), Find<TextBlock>(view, "SaveStatus").Text);
            Assert.Equal(original, JsonSerializer.Serialize(configuration.Settings));
            Assert.Equal(0, dialogs.ConfirmationRequests);
            dialogs.ImportException = null;
            dialogs.ImportSource = CreateImportStream(CreateImportedSettings());

            Assert.True(await view.ImportSettingsAsync());

            Assert.Equal("imported-model", store.Load().Settings.Translation.Model);
            Assert.Equal(2, dialogs.ImportRequests);
        }
        finally
        {
            window.Close();
            Localization.SetLanguage(originalLanguage);
            Application.Current.RequestedThemeVariant = originalTheme;
        }
    }

    /// <summary>应用通知失败时恢复已经写入磁盘的旧设置，并允许重新导入。</summary>
    [AvaloniaFact]
    public async Task ImportApplyFailureRestoresPersistedSettingsAndAllowsRetry()
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = CreateLocalConfiguration();
        var store = new ConfigurationService(directory.Path);
        await store.SaveAsync(configuration);
        await using var runtime = new PanelRuntime(store, configuration);
        var original = JsonSerializer.Serialize(configuration.Settings);
        var failNextChange = true;
        runtime.Changed += _ =>
        {
            if (failNextChange)
            {
                failNextChange = false;
                throw new IOException("Controlled settings application failure");
            }
        };
        var dialogs = new ControlledSettingsTransferDialogs { ImportSource = CreateImportStream(CreateImportedSettings()) };
        var view = new SettingsView(runtime, new TextBlock(), true, dialogs);
        var window = new Window { Content = view, Width = 1020, Height = 760 };
        var originalLanguage = Localization.SelectedLanguageID;
        var originalTheme = Application.Current!.RequestedThemeVariant;
        try
        {
            Show(window);

            Assert.False(await view.ImportSettingsAsync());

            Assert.Equal(original, JsonSerializer.Serialize(configuration.Settings));
            Assert.Equal(original, JsonSerializer.Serialize(store.Load().Settings));
            Assert.Contains(Localization.Get("Settings.ImportFailed"), Find<TextBlock>(view, "SaveStatus").Text);
            Assert.True(window.IsVisible);
            dialogs.ImportSource = CreateImportStream(CreateImportedSettings());

            Assert.True(await view.ImportSettingsAsync());

            Assert.Equal("imported-model", store.Load().Settings.Translation.Model);
        }
        finally
        {
            window.Close();
            Localization.SetLanguage(originalLanguage);
            Application.Current.RequestedThemeVariant = originalTheme;
        }
    }

    /// <summary>有效备份可修复旧无效翻译设置；应用失败时仍可恢复真实旧配置。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidImportRepairsInvalidExistingSettingsOrRestoresThemAfterApplyFailure(bool failApplication)
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = CreateLocalConfiguration();
        configuration.Settings.Translation.Enabled = true;
        configuration.Settings.Translation.Model = "";
        var store = new ConfigurationService(directory.Path);
        await store.SaveAsync(configuration);
        await using var runtime = new PanelRuntime(store, configuration);
        var original = JsonSerializer.Serialize(configuration.Settings);
        var failNextChange = failApplication;
        runtime.Changed += _ =>
        {
            if (failNextChange)
            {
                failNextChange = false;
                throw new IOException("Controlled application failure with invalid previous settings");
            }
        };
        var dialogs = new ControlledSettingsTransferDialogs { ImportSource = CreateImportStream(CreateImportedSettings()) };
        var view = new SettingsView(runtime, new TextBlock(), true, dialogs);
        var window = new Window { Content = view, Width = 1020, Height = 760 };
        var originalLanguage = Localization.SelectedLanguageID;
        var originalTheme = Application.Current!.RequestedThemeVariant;
        var saved = false;
        view.Saved += () => saved = true;
        try
        {
            Show(window);

            Assert.Equal(!failApplication, await view.ImportSettingsAsync());

            Assert.Equal(1, dialogs.ConfirmationRequests);
            if (failApplication)
            {
                Assert.Equal(original, JsonSerializer.Serialize(configuration.Settings));
                Assert.Equal(original, JsonSerializer.Serialize(store.Load().Settings));
                Assert.False(saved);
                var status = Find<TextBlock>(view, "SaveStatus").Text;
                Assert.Contains(Localization.Get("Settings.ImportFailed"), status);
                Assert.DoesNotContain(Localization.Get("Settings.InvalidSettingsFile"), status);
                Assert.DoesNotContain(Localization.Get("Settings.ImportRestoreFailed"), status);
            }
            else
            {
                Assert.Equal("imported-model", configuration.Settings.Translation.Model);
                Assert.Equal("imported-model", store.Load().Settings.Translation.Model);
                Assert.Equal("  imported-key-测试  ", store.Load().Settings.Translation.ApiKey);
                Assert.False(configuration.Settings.Translation.Enabled);
                Assert.True(saved);
            }
        }
        finally
        {
            window.Close();
            Localization.SetLanguage(originalLanguage);
            Application.Current.RequestedThemeVariant = originalTheme;
        }
    }

    /// <summary>保存和恢复文件均失败时仍还原运行设置，移除写入障碍后可重试导入。</summary>
    [AvaloniaFact]
    public async Task ImportPersistenceFailureRestoresSettingsAndAllowsRetry()
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = CreateLocalConfiguration();
        var store = new ConfigurationService(directory.Path);
        await store.SaveAsync(configuration);
        await using var runtime = new PanelRuntime(store, configuration);
        var original = JsonSerializer.Serialize(configuration.Settings);
        var dialogs = new ControlledSettingsTransferDialogs { ImportSource = CreateImportStream(CreateImportedSettings()) };
        var view = new SettingsView(runtime, new TextBlock(), true, dialogs);
        var window = new Window { Content = view, Width = 1020, Height = 760 };
        var obstruction = Path.Combine(directory.Path, "settings.json.tmp");
        var originalLanguage = Localization.SelectedLanguageID;
        var originalTheme = Application.Current!.RequestedThemeVariant;
        try
        {
            Show(window);
            Directory.CreateDirectory(obstruction);

            Assert.False(await view.ImportSettingsAsync());

            Assert.Equal(original, JsonSerializer.Serialize(configuration.Settings));
            Assert.Equal(original, JsonSerializer.Serialize(store.Load().Settings));
            Assert.Contains(Localization.Get("Settings.ImportRestoreFailed"), Find<TextBlock>(view, "SaveStatus").Text);
            Assert.True(window.IsVisible);
            Assert.True(Assert.IsAssignableFrom<Control>(view.Content).IsEnabled);
            Directory.Delete(obstruction);
            dialogs.ImportSource = CreateImportStream(CreateImportedSettings());

            Assert.True(await view.ImportSettingsAsync());

            Assert.Equal("imported-model", store.Load().Settings.Translation.Model);
            Assert.Equal("  imported-key-测试  ", store.Load().Settings.Translation.ApiKey);
        }
        finally
        {
            if (Directory.Exists(obstruction))
            {
                Directory.Delete(obstruction);
            }
            window.Close();
            Localization.SetLanguage(originalLanguage);
            Application.Current.RequestedThemeVariant = originalTheme;
        }
    }

    private static PanelConfiguration CreateLocalConfiguration()
    {
        return new()
        {
            Settings = new()
            {
                DefaultAccountId = "local-account",
                SelectedWorkspaceId = "local-workspace",
                Translation = new() { Model = "original-model", ApiKey = "original-key" }
            },
            Accounts = [new() { Id = "local-account", Name = "本地账号" }],
            Workspaces = [new() { Id = "local-workspace", Name = "本地工作区" }]
        };
    }

    private static PanelSettings CreateImportedSettings()
    {
        return new()
        {
            Language = "ja-JP",
            Theme = "dark",
            UseWindowMenuOnMac = true,
            CheckForUpdatesOnStartup = false,
            ColorScheme = new() { Overrides = new() { ["AppPrimaryBrush"] = "#123456" } },
            ShowDanmuPinyin = true,
            MessageTextScale = 1.5,
            Translation = new() { Model = "imported-model", ApiKey = "  imported-key-测试  " }
        };
    }

    private static MemoryStream CreateImportStream(PanelSettings settings)
    {
        return new(JsonSerializer.SerializeToUtf8Bytes(new SettingsShareService().CreatePackage(settings)));
    }

    private static void CaptureDataPageIfRequested(Window window)
    {
        if (Environment.GetEnvironmentVariable("DANMUPANEL_SETTINGS_TRANSFER_SCREENSHOTS") is not { Length: > 0 } directory)
        {
            return;
        }
        Directory.CreateDirectory(directory);
        var originalTheme = Application.Current!.RequestedThemeVariant;
        var originalLanguage = Localization.SelectedLanguageID;
        try
        {
            Localization.SetLanguage("zh-CN");
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                Application.Current.RequestedThemeVariant = theme;
                window.UpdateLayout();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                Dispatcher.UIThread.RunJobs();
                using var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
                frame.Save(Path.Combine(directory, "settings-data-" + theme.Key.ToString()!.ToLowerInvariant() + ".png"));
            }
        }
        finally
        {
            Localization.SetLanguage(originalLanguage);
            Application.Current.RequestedThemeVariant = originalTheme;
        }
    }

    private static void Show(Window window)
    {
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void SelectSection(SettingsView view, string section)
    {
        Click(view, "SettingsSection" + section);
    }

    private static void Click(SettingsView view, string name)
    {
        Find<Button>(view, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static T Find<T>(SettingsView view, string name) where T : Control
    {
        return view.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    }

    private static TextBox FindModel(SettingsView view, string value)
    {
        return view.GetVisualDescendants().OfType<TextBox>().Single(control => control.Text == value);
    }

    private static TextBox FindApiKey(SettingsView view)
    {
        return view.GetVisualDescendants().OfType<TextBox>().Single(control => control.PasswordChar == '●');
    }
}
