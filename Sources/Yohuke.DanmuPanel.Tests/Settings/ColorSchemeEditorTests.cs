using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Xunit;
using Yohuke.DanmuPanel.Models.Appearance;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Appearance;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Views.Components;
using Yohuke.DanmuPanel.Views.Settings;

namespace Yohuke.DanmuPanel.Tests.Settings;

/// <summary>验证配色草稿、命名方案快照和全局设置保存。</summary>
public sealed class ColorSchemeEditorTests
{
    /// <summary>舰队边框和粉丝牌底色在各自分组提供独立输入，保存后可分别恢复默认。</summary>
    [AvaloniaFact]
    public async Task GuardBordersAndMedalBackgroundsHaveIndependentInputsAndPersistence()
    {
        using var directory = new MessageStoreTestDirectory();
        using var colors = new ColorSchemeService();
        var settings = new PanelSettings();
        var editor = new ColorSchemeEditor(settings, colors);
        var levels = new[]
        {
            (Border: "GuardCaptainBrush", Medal: "MedalCaptainBackgroundBrush", Default: "#4F8FDD"),
            (Border: "GuardAdmiralBrush", Medal: "MedalAdmiralBackgroundBrush", Default: "#A46EE8"),
            (Border: "GuardGovernorBrush", Medal: "MedalGovernorBackgroundBrush", Default: "#E9A23B")
        };
        var window = new Window { Width = 900, Height = 900, Content = new ScrollViewer { Content = editor } };
        try
        {
            window.Show();
            foreach (var (borderKey, medalKey, defaultColor) in levels)
            {
                Assert.Equal(Color.Parse(defaultColor), colors.GetDefaultColor(Application.Current!, borderKey));
                Assert.Equal(Color.Parse(defaultColor), colors.GetDefaultColor(Application.Current!, medalKey));
                Assert.NotNull(Find<ColorInput>(Find<StackPanel>(editor, "Colors_Borders"), "ColorInput_" + borderKey));
                Assert.NotNull(Find<ColorInput>(Find<StackPanel>(editor, "Colors_Medals"), "ColorInput_" + medalKey));
                Input(editor, borderKey).Text = "#123456";
                Pump(window);
                Assert.False(settings.ColorScheme.Overrides.ContainsKey(medalKey));
                Input(editor, medalKey).Text = "#FEDCBA";
                Pump(window);
                Assert.Equal("#123456", settings.ColorScheme.Overrides[borderKey]);
                Assert.Equal("#FEDCBA", settings.ColorScheme.Overrides[medalKey]);
            }
            Assert.True(editor.TryCommit(out var error));
            Assert.Null(error);
            var store = new ConfigurationService(directory.Path);
            await store.SaveAsync(new() { Settings = settings });
            var restored = store.Load().Settings.ColorScheme;
            foreach (var (borderKey, medalKey, _) in levels)
            {
                Assert.Equal("#123456", restored.Overrides[borderKey]);
                Assert.Equal("#FEDCBA", restored.Overrides[medalKey]);
                Click(Find<ColorInput>(editor, "ColorInput_" + medalKey), "ResetColorButton");
                Pump(window);
                Assert.False(settings.ColorScheme.Overrides.ContainsKey(medalKey));
                Assert.Equal("#123456", settings.ColorScheme.Overrides[borderKey]);
                Assert.Equal("#123456", Input(editor, borderKey).Text);
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>低价档均提供独立输入，预览和恢复默认不会改动原有蓝色档。</summary>
    [AvaloniaFact]
    public async Task LowPriceTierColorsCanBeEditedAndResetIndependently()
    {
        using var directory = new MessageStoreTestDirectory();
        using var colors = new ColorSchemeService();
        var settings = new PanelSettings();
        var editor = new ColorSchemeEditor(settings, colors);
        var expected = new Dictionary<string, string>
        {
            ["SuperChatValueUnder30Brush"] = "#8B9BA8",
            ["GiftValueUnder1Brush"] = "#8B9BA8",
            ["GiftValue1Brush"] = "#65B87A",
            ["GiftValue10Brush"] = "#39B5C8"
        };
        var window = new Window { Width = 900, Height = 900, Content = new ScrollViewer { Content = editor } };
        try
        {
            window.Show();
            foreach (var (key, defaultColor) in expected)
            {
                Assert.Equal(Color.Parse(defaultColor), colors.GetDefaultColor(Application.Current!, key));
                Input(editor, key).Text = "#AB12CD";
                Pump(window);
                Assert.Equal("#AB12CD", settings.ColorScheme.Overrides[key]);
            }
            Assert.False(settings.ColorScheme.Overrides.ContainsKey("SuperChatValueUnder50Brush"));
            Assert.False(settings.ColorScheme.Overrides.ContainsKey("GiftValueUnder50Brush"));
            Assert.True(editor.TryCommit(out var error));
            Assert.Null(error);
            var store = new ConfigurationService(directory.Path);
            await store.SaveAsync(new() { Settings = settings });
            var restored = store.Load().Settings;
            Assert.All(expected.Keys, key => Assert.Equal("#AB12CD", restored.ColorScheme.Overrides[key]));
            Click(editor, "ResetAllColors");
            Assert.Empty(settings.ColorScheme.Overrides);
            Assert.All(expected.Keys, key => Assert.True(string.IsNullOrEmpty(Input(editor, key).Text)));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>有效输入即时预览，方案保存独立快照并能够随设置持久化。</summary>
    [AvaloniaFact]
    public async Task SavedSchemeIsIndependentAndRoundTripsWithSettings()
    {
        using var directory = new MessageStoreTestDirectory();
        using var colors = new ColorSchemeService();
        var settings = new PanelSettings { Theme = "dark" };
        var editor = new ColorSchemeEditor(settings, colors);
        var changes = 0;
        editor.Changed += () => changes++;
        var window = new Window { Width = 900, Height = 900, Content = new ScrollViewer { Content = editor } };
        Flyout? pickerFlyout = null;
        try
        {
            window.Show();
            var originalInput = Find<ColorInput>(editor, "ColorInput_MessageOriginalBrush");
            originalInput.BringIntoView();
            Pump(window);
            var pickerButton = Find<Button>(originalInput, "ColorPickerButton");
            pickerButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            pickerFlyout = Assert.IsType<Flyout>(pickerButton.Flyout);
            var picker = Assert.IsType<ColorView>(pickerFlyout.Content);
            picker.Color = Colors.White;
            Pump(window);
            Assert.Equal("#FFFFFF", settings.ColorScheme.Overrides["MessageOriginalBrush"]);
            picker.Color = Color.Parse("#80112233");
            Pump(window);
            Assert.Equal("#80112233", settings.ColorScheme.Overrides["MessageOriginalBrush"]);
            Assert.Equal("#80112233", Input(editor, "MessageOriginalBrush").Text);
            CaptureIfRequested(TopLevel.GetTopLevel(picker)!, "picker");
            pickerFlyout.Hide();
            Pump(window);
            Assert.True(changes > 0);
            Find<TextBox>(editor, "ColorSchemeName").Text = "夜间";
            Click(editor, "SaveColorSchemeAs");
            var saved = Assert.Single(settings.ColorScheme.SavedSchemes);
            Assert.Equal("dark", saved.Theme);
            Assert.Equal("#80112233", saved.Colors["MessageOriginalBrush"]);
            CaptureIfRequested(window, "editor");
            Input(editor, "MessageOriginalBrush").Text = "#445566";
            Pump(window);
            Assert.Equal("#80112233", saved.Colors["MessageOriginalBrush"]);
            var selector = Find<ComboBox>(editor, "ColorSchemeSelector");
            selector.SelectedIndex = 0;
            Assert.Null(settings.ColorScheme.SelectedSchemeId);
            Assert.Equal("#445566", settings.ColorScheme.Overrides["MessageOriginalBrush"]);
            Click(editor, "ResetAllColors");
            Assert.Empty(settings.ColorScheme.Overrides);
            settings.Theme = "light";
            selector.SelectedItem = saved;
            Assert.Equal("dark", settings.Theme);
            Assert.Equal("#80112233", settings.ColorScheme.Overrides["MessageOriginalBrush"]);
            Assert.NotSame(saved.Colors, settings.ColorScheme.Overrides);
            var changesAfterSelection = changes;
            Pump(window);
            Assert.Equal(changesAfterSelection, changes);
            Assert.Equal("#80112233", settings.ColorScheme.Overrides["MessageOriginalBrush"]);
            Assert.True(editor.TryCommit(out var error));
            Assert.Null(error);
            var store = new ConfigurationService(directory.Path);
            await store.SaveAsync(new() { Settings = settings });
            var restored = store.Load().Settings.ColorScheme;
            Assert.Equal(saved.Id, restored.SelectedSchemeId);
            Assert.Equal("#80112233", Assert.Single(restored.SavedSchemes).Colors["MessageOriginalBrush"]);
        }
        finally
        {
            pickerFlyout?.Hide();
            window.Close();
        }
    }

    /// <summary>无效 HEX 保留原始输入并阻止保存或方案切换，刷新主题不会覆盖草稿。</summary>
    [AvaloniaFact]
    public void InvalidDraftSurvivesThemeRefreshAndBlocksSchemeSwitch()
    {
        using var colors = new ColorSchemeService();
        var saved = new SavedColorScheme { Id = "saved", Name = "已有方案", Colors = new() { ["MessageTranslationBrush"] = "#ABCDEF" } };
        var settings = new PanelSettings
        {
            ColorScheme = new()
            {
                SelectedSchemeId = saved.Id, SavedSchemes = [saved],
                Overrides = new(saved.Colors)
            }
        };
        var editor = new ColorSchemeEditor(settings, colors);
        var window = new Window { Width = 900, Height = 900, Content = editor };
        var originalTheme = Application.Current!.RequestedThemeVariant;
        try
        {
            window.Show();
            var input = Input(editor, "MessageTranslationBrush");
            input.Text = "#FF";
            Pump(window);
            Assert.False(editor.TryCommit(out var error));
            Assert.NotNull(error);
            Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
            editor.RefreshDefaults();
            Assert.Equal("#FF", input.Text);
            Find<ComboBox>(editor, "ColorSchemeSelector").SelectedIndex = 0;
            Assert.Equal(saved.Id, settings.ColorScheme.SelectedSchemeId);
            Assert.Equal("#FF", input.Text);
            Assert.Equal("#ABCDEF", settings.ColorScheme.Overrides["MessageTranslationBrush"]);
        }
        finally
        {
            window.Close();
            Application.Current.RequestedThemeVariant = originalTheme;
        }
    }

    /// <summary>更新与删除只影响目标方案，恢复默认保留方案库。</summary>
    [AvaloniaFact]
    public void UpdateDeleteAndResetKeepSavedSnapshotsConsistent()
    {
        using var colors = new ColorSchemeService();
        var settings = new PanelSettings();
        var editor = new ColorSchemeEditor(settings, colors);
        var window = new Window { Width = 900, Height = 900, Content = editor };
        try
        {
            window.Show();
            Input(editor, "MessageTranslationBrush").Text = "#112233";
            Pump(window);
            Find<TextBox>(editor, "ColorSchemeName").Text = "第一套";
            Click(editor, "SaveColorSchemeAs");
            var saved = Assert.Single(settings.ColorScheme.SavedSchemes);
            Input(editor, "MessageTranslationBrush").Text = "#AABBCC";
            Pump(window);
            Click(editor, "UpdateColorScheme");
            Assert.Equal("#AABBCC", saved.Colors["MessageTranslationBrush"]);
            Click(editor, "ResetAllColors");
            Assert.Empty(settings.ColorScheme.Overrides);
            Assert.Single(settings.ColorScheme.SavedSchemes);
            Find<ComboBox>(editor, "ColorSchemeSelector").SelectedItem = saved;
            Click(editor, "DeleteColorScheme");
            Assert.Empty(settings.ColorScheme.SavedSchemes);
            Assert.Null(settings.ColorScheme.SelectedSchemeId);
            Assert.Equal("#AABBCC", settings.ColorScheme.Overrides["MessageTranslationBrush"]);
        }
        finally
        {
            window.Close();
        }
    }

    private static TextBox Input(Control editor, string key)
    {
        return Find<TextBox>(Find<ColorInput>(editor, "ColorInput_" + key), "ColorHexInput");
    }

    private static T Find<T>(Control control, string name) where T : Control
    {
        return control.GetLogicalDescendants().OfType<T>().Single(value => value.Name == name);
    }

    private static void Click(Control editor, string name)
    {
        Find<Button>(editor, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void CaptureIfRequested(TopLevel topLevel, string name)
    {
        if (Environment.GetEnvironmentVariable("DANMUPANEL_COLOR_SCREENSHOTS") is not { Length: > 0 } directory)
        {
            return;
        }
        Directory.CreateDirectory(directory);
        topLevel.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        Dispatcher.UIThread.RunJobs();
        using var image = topLevel.CaptureRenderedFrame();
        Assert.NotNull(image);
        image.Save(Path.Combine(directory, name + ".png"));
    }
}
