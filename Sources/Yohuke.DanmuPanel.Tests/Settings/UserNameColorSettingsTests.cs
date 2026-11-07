using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Appearance;
using Yohuke.DanmuPanel.Services.Settings;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Views.Components;
using Yohuke.DanmuPanel.Views.Settings;

namespace Yohuke.DanmuPanel.Tests.Settings;

/// <summary>验证三级舰队和房管的用户名颜色独立编辑、恢复和分享。</summary>
public sealed class UserNameColorSettingsTests
{
    /// <summary>四项名称颜色在深浅主题提供默认预览，修改后可持久化和分享，恢复单项不影响其他项目。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RoleNameColorsCanBeConfiguredAndResetIndependently(bool dark)
    {
        using var directory = new MessageStoreTestDirectory();
        var originalTheme = Application.Current!.RequestedThemeVariant;
        Application.Current.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        using var colors = new ColorSchemeService();
        var settings = new PanelSettings();
        var editor = new ColorSchemeEditor(settings, colors);
        var window = new Window { Content = new ScrollViewer { Content = editor }, Width = 1000, Height = 800 };
        var expected = new Dictionary<string, string>
        {
            ["GuardCaptainUserNameBrush"] = "#123456",
            ["GuardAdmiralUserNameBrush"] = "#654321",
            ["GuardGovernorUserNameBrush"] = "#246813",
            ["RoomAdminUserNameBrush"] = "#135790"
        };
        try
        {
            window.Show();
            foreach (var (key, value) in expected)
            {
                Assert.NotNull(colors.GetDefaultColor(Application.Current, key));
                var input = FindColor(editor, key);
                input.GetVisualDescendants().OfType<TextBox>().Single(text => text.Name == "ColorHexInput").Text = value;
                Pump(window);
                Assert.Equal(value, settings.ColorScheme.Overrides[key]);
            }
            Assert.Equal(4, settings.ColorScheme.Overrides.Count);
            Assert.True(editor.TryCommit(out var error));
            Assert.Null(error);
            var store = new ConfigurationService(directory.Path);
            await store.SaveAsync(new() { Settings = settings });
            var restored = store.Load().Settings;
            await using var stream = new MemoryStream();
            var sharing = new SettingsShareService();
            await sharing.ExportAsync(restored, stream);
            stream.Position = 0;
            var imported = await sharing.ImportAsync(stream);
            Assert.All(expected, pair => Assert.Equal(pair.Value, imported.ColorScheme.Overrides[pair.Key]));

            FindColor(editor, "GuardCaptainUserNameBrush").GetVisualDescendants().OfType<Button>()
                .Single(button => button.Name == "ResetColorButton").Command!.Execute(null);
            Pump(window);
            Assert.False(settings.ColorScheme.Overrides.ContainsKey("GuardCaptainUserNameBrush"));
            Assert.All(expected.Where(pair => pair.Key != "GuardCaptainUserNameBrush"),
                pair => Assert.Equal(pair.Value, settings.ColorScheme.Overrides[pair.Key]));
            Assert.False(settings.ColorScheme.Overrides.ContainsKey("MedalCaptainBackgroundBrush"));
            Assert.False(settings.ColorScheme.Overrides.ContainsKey("GuardCaptainBrush"));
        }
        finally
        {
            window.Close();
            Application.Current.RequestedThemeVariant = originalTheme;
        }
    }

    private static ColorInput FindColor(Control root, string key)
    {
        return root.GetVisualDescendants().OfType<ColorInput>().Single(input => input.Name == "ColorInput_" + key);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
