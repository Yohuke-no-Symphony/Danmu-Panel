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
            var term = Assert.Single(view.GetVisualDescendants().OfType<DataGrid>().Single().ItemsSource.Cast<GlossaryRow>());
            term.Source = "主播";
            term.Target = "配信者";
            Assert.True(await view.SaveAsync());
            var restored = runtime.ConfigurationStore.Load().Settings.Translation;
            Assert.Equal("ja-JP", restored.TargetLanguage);
            Assert.Equal("ja-JP", Assert.Single(restored.Glossary).TargetLanguage);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>同一原词共用一行，切换语言提交旧列编辑并保存各语言的译法。</summary>
    [AvaloniaFact]
    public async Task LanguageSwitchCommitsEditsAndKeepsTranslationsInOneRow()
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = new PanelConfiguration
        {
            Settings = new()
            {
                Translation = new()
                {
                    TargetLanguage = "ja-JP",
                    Glossary =
                    [
                        new() { Source = "主播", Target = "配信者", TargetLanguage = "ja-JP" },
                        new() { Source = "主播", Target = "播主", TargetLanguage = "zh-CN" }
                    ]
                }
            }
        };
        await using var runtime = new PanelRuntime(new(directory.Path), configuration);
        var view = new SettingsView(runtime, new TextBlock());
        var window = OpenTranslationSettings(view);
        try
        {
            var grid = view.GetVisualDescendants().OfType<DataGrid>().Single();
            var row = Assert.Single(grid.ItemsSource.Cast<object>());
            Assert.Equal(2, grid.Columns.Count);
            var language = Find<ComboBox>(view, "TranslationTargetLanguage");
            language.SelectedItem = language.Items.Cast<TranslationLanguageOption>().Single(item => item.Code == "en-US");
            var english = EditGlossaryCell(grid, row, 1);
            Assert.Equal("", english.Text);
            english.Text = "streamer";

            language.SelectedItem = language.Items.Cast<TranslationLanguageOption>().Single(item => item.Code == "ja-JP");
            Assert.Same(row, Assert.Single(grid.ItemsSource.Cast<object>()));
            var japanese = EditGlossaryCell(grid, row, 1);
            Assert.Equal("配信者", japanese.Text);
            japanese.Text = "ライバー";

            language.SelectedItem = language.Items.Cast<TranslationLanguageOption>().Single(item => item.Code == "zh-CN");
            var chinese = EditGlossaryCell(grid, row, 1);
            Assert.Equal("播主", chinese.Text);
            Assert.Equal("配信者", configuration.Settings.Translation.Glossary.Single(entry => entry.TargetLanguage == "ja-JP").Target);
            Assert.True(await view.SaveAsync());

            var restored = runtime.ConfigurationStore.Load().Settings.Translation;
            Assert.Equal("zh-CN", restored.TargetLanguage);
            Assert.Equal(3, restored.Glossary.Count);
            Assert.Equal("streamer", restored.Glossary.Single(entry => entry.TargetLanguage == "en-US").Target);
            Assert.Equal("ライバー", restored.Glossary.Single(entry => entry.TargetLanguage == "ja-JP").Target);
            Assert.Equal("播主", restored.Glossary.Single(entry => entry.TargetLanguage == "zh-CN").Target);
            Assert.All(restored.Glossary, entry => Assert.Equal("主播", entry.Source));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>当前语言没有译法时，其他语言的词条仍可保存和重新加载。</summary>
    [AvaloniaFact]
    public async Task BlankCurrentLanguagePreservesTranslationsAndSourceEditsForOtherLanguages()
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = new PanelConfiguration
        {
            Settings = new()
            {
                Translation = new()
                {
                    TargetLanguage = "en-US",
                    Glossary = [new() { Source = "主播", Target = "配信者", TargetLanguage = "ja-JP" }]
                }
            }
        };
        await using var runtime = new PanelRuntime(new(directory.Path), configuration);
        var view = new SettingsView(runtime, new TextBlock());
        var window = OpenTranslationSettings(view);
        try
        {
            var grid = view.GetVisualDescendants().OfType<DataGrid>().Single();
            var row = Assert.Single(grid.ItemsSource.Cast<object>());
            Assert.Equal("", EditGlossaryCell(grid, row, 1).Text);
            EditGlossaryCell(grid, row, 0).Text = "UP主";
            Assert.True(await view.SaveAsync());
            var restored = runtime.ConfigurationStore.Load().Settings.Translation;
            var term = Assert.Single(restored.Glossary);
            Assert.Equal("UP主", term.Source);
            Assert.Equal("配信者", term.Target);
            Assert.Equal("ja-JP", term.TargetLanguage);
            window.Close();

            var reopened = new SettingsView(runtime, new TextBlock());
            window = OpenTranslationSettings(reopened);
            var language = Find<ComboBox>(reopened, "TranslationTargetLanguage");
            language.SelectedItem = language.Items.Cast<TranslationLanguageOption>().Single(item => item.Code == "ja-JP");
            grid = reopened.GetVisualDescendants().OfType<DataGrid>().Single();
            Assert.Equal("配信者", EditGlossaryCell(grid, Assert.Single(grid.ItemsSource.Cast<object>()), 1).Text);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>删除一行会删除该原词的全部语言译法，并保留其他原词。</summary>
    [AvaloniaFact]
    public async Task RemovingTermRemovesAllItsTranslations()
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = new PanelConfiguration
        {
            Settings = new()
            {
                Translation = new()
                {
                    Glossary =
                    [
                        new() { Source = "主播", Target = "配信者", TargetLanguage = "ja-JP" },
                        new() { Source = "主播", Target = "streamer", TargetLanguage = "en-US" },
                        new() { Source = "礼物", Target = "gift", TargetLanguage = "en-US" }
                    ]
                }
            }
        };
        await using var runtime = new PanelRuntime(new(directory.Path), configuration);
        var view = new SettingsView(runtime, new TextBlock());
        var window = OpenTranslationSettings(view);
        try
        {
            var grid = view.GetVisualDescendants().OfType<DataGrid>().Single();
            Assert.Equal(2, grid.ItemsSource.Cast<object>().Count());
            grid.SelectedItem = grid.ItemsSource.Cast<object>().First();
            await Assert.IsAssignableFrom<IAsyncRelayCommand>(Find<Button>(view, "RemoveGlossaryTerm").Command).ExecuteAsync(null);
            Assert.True(await view.SaveAsync());
            Assert.Equal("礼物", Assert.Single(runtime.ConfigurationStore.Load().Settings.Translation.Glossary).Source);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>术语仍需填写原词和至少一种译法。</summary>
    [AvaloniaTheory]
    [InlineData(0, "主播")]
    [InlineData(1, "配信者")]
    public async Task IncompleteTermCannotSave(int column, string text)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var view = new SettingsView(runtime, new TextBlock());
        var window = OpenTranslationSettings(view);
        try
        {
            await Assert.IsAssignableFrom<IAsyncRelayCommand>(Find<Button>(view, "AddGlossaryTerm").Command).ExecuteAsync(null);
            var grid = view.GetVisualDescendants().OfType<DataGrid>().Single();
            EditGlossaryCell(grid, Assert.Single(grid.ItemsSource.Cast<object>()), column).Text = text;
            Assert.False(await view.SaveAsync());
            Assert.Empty(runtime.Configuration.Settings.Translation.Glossary);
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
    private static TextBox EditGlossaryCell(DataGrid grid, object row, int column)
    {
        grid.SelectedItem = row;
        grid.CurrentColumn = grid.Columns[column];
        grid.ScrollIntoView(row, grid.CurrentColumn);
        grid.UpdateLayout();
        Assert.True(grid.BeginEdit());
        return Assert.Single(grid.GetVisualDescendants().OfType<TextBox>());
    }

    private static Window OpenTranslationSettings(SettingsView view)
    {
        var window = new Window { Content = view, Width = 1020, Height = 900 };
        window.Show();
        Find<Button>(view, "SettingsSectionTranslation").ExecuteBoundCommand();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return window;
    }

    private static T Find<T>(Control view, string name) where T : Control
    {
        return view.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    }
}
