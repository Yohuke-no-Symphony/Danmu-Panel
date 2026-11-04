using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Lucide.Avalonia;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Data.Sqlite;
using Xunit;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Views;
using Yohuke.DanmuPanel.Views.Settings;

namespace Yohuke.DanmuPanel.Tests.Settings;

public sealed class SettingsInteractionTests
{
    [AvaloniaFact]
    public async Task SettingsPagesRenderWithoutNetwork()
    {
        var directory = Path.Combine(Path.GetTempPath(), "panel-settings-preview-" + Guid.NewGuid().ToString("N"));
        var configuration = new PanelConfiguration
        {
            Accounts = [new() { Name = "夜更音乐电台" }, new() { Name = "Sakura" }],
            Settings = new()
            {
                Translation = new()
                {
                    Model = "translation-model",
                    TargetLanguage = "ja-JP",
                    Glossary = [new() { Source = "上舰", Target = "メンバー加入", TargetLanguage = "ja-JP" }]
                }
            }
        };
        configuration.Settings.DefaultAccountId = configuration.Accounts[0].Id;
        var runtime = new PanelRuntime(new(directory), configuration);
        var originalLanguage = Localization.SelectedLanguageID;
        var originalTheme = Application.Current!.RequestedThemeVariant;
        Localization.SetLanguage("zh-CN");
        Application.Current.RequestedThemeVariant = ThemeVariant.Light;
        var view = new SettingsView(runtime, new AccountSettingsView(runtime));
        var window = new Window { Content = view, Width = 1020, Height = 900 };
        try
        {
            window.Show();
            foreach (var section in new[] { "Appearance", "Translation", "Accounts" })
            {
                Click(view, "SettingsSection" + section);
                window.UpdateLayout();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                Dispatcher.UIThread.RunJobs();
                Assert.Contains(view.GetVisualDescendants().OfType<Button>(), button =>
                    button.Name == "SettingsSection" + section && button.Classes.Contains("selected"));
                if (Environment.GetEnvironmentVariable("DANMUPANEL_SETTINGS_SCREENSHOTS") is { Length: > 0 } output)
                {
                    Directory.CreateDirectory(output);
                    using var frame = window.CaptureRenderedFrame();
                    Assert.NotNull(frame);
                    frame.Save(Path.Combine(output, section.ToLowerInvariant() + ".png"));
                    if (section == "Accounts")
                    {
                        Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
                        window.UpdateLayout();
                        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                        Dispatcher.UIThread.RunJobs();
                        using var darkFrame = window.CaptureRenderedFrame();
                        Assert.NotNull(darkFrame);
                        darkFrame.Save(Path.Combine(output, "accounts-dark.png"));
                    }
                }
            }
        }
        finally
        {
            window.Close();
            await runtime.DisposeAsync();
            Localization.SetLanguage(originalLanguage);
            Application.Current.RequestedThemeVariant = originalTheme;
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [AvaloniaFact]
    public async Task SidebarNavigationRetainsTranslationDraftAndUsesHeaderSave()
    {
        var directory = Path.Combine(Path.GetTempPath(), "panel-settings-ui-" + Guid.NewGuid().ToString("N"));
        var configuration = new PanelConfiguration { Settings = new() { Translation = new() { Model = "before" } } };
        var runtime = new PanelRuntime(new(directory), configuration);
        var view = new SettingsView(runtime, new TextBlock());
        var window = new Window { Content = view, Width = 1020, Height = 760 };
        try
        {
            window.Show();
            Click(view, "SettingsSectionTranslation");
            var model = view.GetVisualDescendants().OfType<TextBox>().Single(value => value.Text == "before");
            model.Text = "after";
            Click(view, "SettingsSectionReading");
            Click(view, "SettingsSectionTranslation");
            Assert.Contains(view.GetVisualDescendants().OfType<TextBox>(), value => ReferenceEquals(value, model));
            Assert.Empty(view.GetVisualDescendants().OfType<TabControl>());
            var save = view.GetVisualDescendants().OfType<Button>().Single(value => value.Name == "SaveSettingsButton");
            Assert.Contains("primaryButton", save.Classes);
            Assert.Contains(save.GetVisualDescendants().OfType<LucideIcon>(), icon => icon.Kind == LucideIconKind.Save);
            Assert.True(await view.SaveAsync());
            Assert.Equal("after", runtime.ConfigurationStore.Load().Settings.Translation.Model);
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
    public async Task GlossaryRowMenuDeletesTheClickedTermInsteadOfSelectedTerm()
    {
        var directory = Path.Combine(Path.GetTempPath(), "panel-glossary-ui-" + Guid.NewGuid().ToString("N"));
        var configuration = new PanelConfiguration
        {
            Settings = new()
            {
                Translation = new()
                {
                    Glossary =
                    [
                        new() { Source = "first", Target = "一", TargetLanguage = "zh-CN" },
                        new() { Source = "second", Target = "二", TargetLanguage = "zh-CN" }
                    ]
                }
            }
        };
        var runtime = new PanelRuntime(new(directory), configuration);
        var view = new SettingsView(runtime, new TextBlock());
        var window = new Window { Content = view, Width = 1020, Height = 1400 };
        try
        {
            window.Show();
            Click(view, "SettingsSectionTranslation");
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var grid = view.GetVisualDescendants().OfType<DataGrid>().Single();
            var terms = grid.ItemsSource.Cast<object>().ToArray();
            grid.SelectedItem = terms[0];
            grid.ScrollIntoView(terms[1], grid.Columns[0]);
            window.UpdateLayout();
            var row = grid.GetVisualDescendants().OfType<DataGridRow>().Single(value => ReferenceEquals(value.DataContext, terms[1]));
            var delete = Assert.IsType<MenuItem>(Assert.Single(row.ContextMenu!.Items));
            Assert.IsType<LucideIcon>(delete.Icon);
            delete.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Same(terms[0], Assert.Single(grid.ItemsSource.Cast<object>()));
            Assert.True(await view.SaveAsync());
            Assert.Equal("first", Assert.Single(runtime.ConfigurationStore.Load().Settings.Translation.Glossary).Source);
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
    public async Task AccountRowsSaveAllDraftsAndDefaultActionTargetsItsOwnAccount()
    {
        var directory = Path.Combine(Path.GetTempPath(), "panel-accounts-ui-" + Guid.NewGuid().ToString("N"));
        var first = new BilibiliAccount { Name = "first", AutoNameOnLogin = true };
        var second = new BilibiliAccount { Name = "second", AutoNameOnLogin = true };
        var configuration = new PanelConfiguration { Accounts = [first, second] };
        var runtime = new PanelRuntime(new(directory), configuration);
        var view = new AccountSettingsView(runtime);
        var window = new Window { Content = view, Width = 740, Height = 760 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var rows = view.GetVisualDescendants().OfType<AccountSettingsRow>().ToArray();
            Assert.Equal(2, rows.Length);
            var firstName = rows[0].GetVisualDescendants().OfType<TextBox>().Single(value => value.Name == "AccountNameEditor");
            var secondName = rows[1].GetVisualDescendants().OfType<TextBox>().Single(value => value.Name == "AccountNameEditor");
            firstName.Text = "first edited";
            secondName.Text = "second edited";
            Assert.Empty(view.GetVisualDescendants().OfType<Expander>());
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(), button =>
                button.Content is LucideIcon { Kind: LucideIconKind.Plus });
            Assert.All(rows, row => Assert.Equal(2, row.GetVisualDescendants().OfType<Grid>()
                .Single(grid => grid.Name == "AccountRowLayout").RowDefinitions.Count));
            var defaults = rows[1].GetVisualDescendants().OfType<RadioButton>().Single();
            await Assert.IsAssignableFrom<IAsyncRelayCommand>(defaults.Command).ExecuteAsync(null);
            await view.SaveEditsAsync();
            var restored = runtime.ConfigurationStore.Load();
            Assert.Equal("first edited", restored.Accounts[0].Name);
            Assert.Equal("second edited", restored.Accounts[1].Name);
            Assert.False(restored.Accounts[0].AutoNameOnLogin);
            Assert.Equal(second.Id, restored.Settings.DefaultAccountId);
            await Assert.IsAssignableFrom<IAsyncRelayCommand>(defaults.Command).ExecuteAsync(null);
            Assert.Equal(second.Id, runtime.Configuration.Settings.DefaultAccountId);
            Assert.True(defaults.IsChecked);
            Assert.All(rows.SelectMany(row => row.GetVisualDescendants().OfType<Button>())
                .Where(button => button.Content is LucideIcon), button => Assert.Contains("iconButton", button.Classes));
        }
        finally
        {
            window.Close();
            await runtime.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    private static void Click(Control view, string name)
    {
        view.GetVisualDescendants().OfType<Button>().Single(button => button.Name == name)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }
}
