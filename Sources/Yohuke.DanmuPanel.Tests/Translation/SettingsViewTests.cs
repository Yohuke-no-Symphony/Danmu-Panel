using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Data.Sqlite;
using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Views.Settings;

namespace Yohuke.DanmuPanel.Tests.Translation;

public sealed class SettingsViewTests
{
    /// <summary>目标语言选择保存代码，并用于新增术语。</summary>
    [AvaloniaFact]
    public async Task TargetLanguageDropdownPersistsCodeAndSetsNewGlossaryLanguage()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var view = new SettingsView(runtime, new TextBlock());
        var window = OpenTranslationSettings(view);
        try
        {
            var language = Find<ComboBox>(view, "TranslationTargetLanguage");
            Assert.Equal("zh-CN", Assert.IsType<TranslationLanguageOption>(language.SelectedItem).Code);
            language.SelectedItem = language.Items.Cast<TranslationLanguageOption>().Single(item => item.Code == "ja-JP");
            window.UpdateLayout();
            Assert.Contains(language.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "(ja-JP)");
            Assert.Contains(language.GetVisualDescendants().OfType<TextBlock>(), text =>
                text.Text == I18n.Localization.Get("Settings.TargetLanguage.Japanese"));
            await Assert.IsAssignableFrom<IAsyncRelayCommand>(Find<Button>(view, "AddGlossaryTerm").Command).ExecuteAsync(null);
            var term = Assert.Single(view.GetVisualDescendants().OfType<DataGrid>().Single().ItemsSource.Cast<GlossaryEntry>());
            Assert.Equal("ja-JP", term.TargetLanguage);
            term.Source = "主播";
            term.Target = "配信者";
            Assert.True(await view.SaveAsync());
            Assert.Equal("ja-JP", runtime.ConfigurationStore.Load().Settings.Translation.TargetLanguage);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>预设地址直接保存，只有自定义项显示可编辑地址。</summary>
    [AvaloniaTheory]
    [InlineData("OpenAI", "https://api.openai.com/v1")]
    [InlineData("DeepSeek", "https://api.deepseek.com")]
    [InlineData("Anthropic (Claude)", "https://api.anthropic.com/v1")]
    public async Task ProviderPresetPersistsItsBaseUrl(string name, string expectedUrl)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var view = new SettingsView(runtime, new TextBlock());
        var window = OpenTranslationSettings(view);
        try
        {
            var provider = Find<ComboBox>(view, "TranslationProvider");
            provider.SelectedItem = provider.Items.Cast<TranslationProviderOption>().Single(item => item.Name == name);
            Assert.False(Find<TextBox>(view, "TranslationCustomBaseUrl").IsVisible);
            Assert.True(await view.SaveAsync());
            Assert.Equal(expectedUrl, runtime.ConfigurationStore.Load().Settings.Translation.BaseUrl);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>旧自定义配置和语言代码不被替换，切换预设保留自定义草稿。</summary>
    [AvaloniaFact]
    public async Task CustomAddressDraftAndUnlistedLanguageSurvivePresetSwitches()
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = new PanelConfiguration
        {
            Settings = new()
            {
                Translation = new() { BaseUrl = "http://localhost:8080/v1", TargetLanguage = "es-MX" }
            }
        };
        await using var runtime = new PanelRuntime(new(directory.Path), configuration);
        var view = new SettingsView(runtime, new TextBlock());
        var window = OpenTranslationSettings(view);
        try
        {
            var provider = Find<ComboBox>(view, "TranslationProvider");
            var custom = Assert.IsType<TranslationProviderOption>(provider.SelectedItem);
            Assert.Null(custom.BaseUrl);
            var address = Find<TextBox>(view, "TranslationCustomBaseUrl");
            Assert.True(address.IsVisible);
            Assert.Equal("http://localhost:8080/v1", address.Text);
            var language = Find<ComboBox>(view, "TranslationTargetLanguage");
            Assert.Contains(language.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "(es-MX)");
            Assert.Contains(language.GetVisualDescendants().OfType<TextBlock>(), text =>
                text.Text == System.Globalization.CultureInfo.GetCultureInfo("es-MX").NativeName);
            address.Text = "http://localhost:9000/v1";
            provider.SelectedItem = provider.Items.Cast<TranslationProviderOption>().Single(item => item.Name == "DeepSeek");
            Assert.False(address.IsVisible);
            provider.SelectedItem = custom;
            Assert.True(address.IsVisible);
            Assert.Equal("http://localhost:9000/v1", address.Text);
            Assert.True(await view.SaveAsync());
            var restored = runtime.ConfigurationStore.Load().Settings.Translation;
            Assert.Equal("http://localhost:9000/v1", restored.BaseUrl);
            Assert.Equal("es-MX", restored.TargetLanguage);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task InvalidEnabledTranslationCannotSaveOrRequestClose()
    {
        var directory = Path.Combine(Path.GetTempPath(), "panel-settings-" + Guid.NewGuid().ToString("N"));
        var configuration = new PanelConfiguration { Settings = new() { Translation = new() { Enabled = true, Model = "" } } };
        var runtime = new PanelRuntime(new(directory), configuration);
        var view = new SettingsView(runtime, new TextBlock());
        var window = new Window { Content = view };
        var requestedClose = false;
        view.Saved += () => requestedClose = true;
        try
        {
            window.Show();
            Assert.False(await view.SaveAsync());
            Assert.False(requestedClose);
            Assert.True(window.IsVisible);
        }
        finally
        {
            window.Close();
            await runtime.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [AvaloniaFact]
    public async Task ValidSettingsPersistTogetherAndNotifyCloseAfterSave()
    {
        var directory = Path.Combine(Path.GetTempPath(), "panel-settings-" + Guid.NewGuid().ToString("N"));
        var configuration = new PanelConfiguration
        {
            Settings = new()
            {
                ShowUserPinyin = true,
                ShowGiftPinyin = true,
                Reading = new() { GiftAutoPinMinimum = 20, GiftReadResetPolicy = GiftReadResetPolicy.NewValueThreshold },
                Translation = new()
                {
                    Enabled = false,
                    Model = "test-model",
                    TargetLanguage = "ja-JP",
                    BacklogMode = TranslationBacklogMode.AllMessages,
                    DisplayMode = TranslationDisplayMode.TranslationOnly,
                    Glossary = [new() { Source = "主播", Target = "配信者", TargetLanguage = "ja-JP" }]
                }
            }
        };
        var store = new ConfigurationService(directory);
        var runtime = new PanelRuntime(store, configuration);
        var view = new SettingsView(runtime, new TextBlock());
        var window = new Window { Content = view };
        var requestedClose = false;
        view.Saved += () => requestedClose = true;
        try
        {
            window.Show();
            Assert.True(await view.SaveAsync());
            Assert.True(requestedClose);
            var restored = store.Load().Settings;
            Assert.Equal(20, restored.Reading.GiftAutoPinMinimum);
            Assert.Equal(GiftReadResetPolicy.NewValueThreshold, restored.Reading.GiftReadResetPolicy);
            Assert.Equal(TranslationBacklogMode.AllMessages, restored.Translation.BacklogMode);
            Assert.Equal(TranslationDisplayMode.TranslationOnly, restored.Translation.DisplayMode);
            Assert.Equal("配信者", Assert.Single(restored.Translation.Glossary).Target);
            Assert.True(restored.ShowUserPinyin);
        }
        finally
        {
            window.Close();
            await runtime.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
    private static Window OpenTranslationSettings(SettingsView view)
    {
        var window = new Window { Content = view, Width = 1020, Height = 900 };
        window.Show();
        Find<Button>(view, "SettingsSectionTranslation").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return window;
    }

    private static T Find<T>(Control view, string name) where T : Control
    {
        return view.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    }
}
