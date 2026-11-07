using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Xunit;
using Yohuke.DanmuPanel.Models.Appearance;
using Yohuke.DanmuPanel.Models.Settings;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Appearance;
using Yohuke.DanmuPanel.Services.Settings;
using Yohuke.DanmuPanel.Tests.Messages;

namespace Yohuke.DanmuPanel.Tests.Settings;

/// <summary>验证特别关注统一颜色迁移为独立的用户名和正文颜色。</summary>
public sealed class SpecialAttentionColorMigrationTests
{
    /// <summary>旧的当前配色和命名方案分别保留原颜色及透明度，并移除旧统一颜色键。</summary>
    [Fact]
    public void LegacyCurrentAndSavedColorsMigrateTogether()
    {
        var colors = JsonSerializer.Deserialize<ColorSchemeSettings>("""
            {
              "Overrides": { "SpecialAttentionMessageBrush": "#12AB34" },
              "SelectedSchemeId": "night",
              "SavedSchemes": [
                { "Id": "night", "Name": "夜间", "Theme": "dark",
                  "Colors": { "SpecialAttentionMessageBrush": "#80445566" } }
              ]
            }
            """)!;

        AssertPair(colors.Overrides, "#12AB34", "#12AB34");
        AssertPair(Assert.Single(colors.SavedSchemes).Colors, "#80445566", "#80445566");
        Assert.Equal("night", colors.SelectedSchemeId);
        ColorSchemeService.Validate(colors);
    }

    /// <summary>已有独立配置优先于旧键，恢复一项默认后重新加载不会被旧统一颜色重新填充。</summary>
    [Fact]
    public void ExplicitColorsAndIndependentResetsSurviveReload()
    {
        var colors = JsonSerializer.Deserialize<ColorSchemeSettings>("""
            { "Overrides": {
                "SpecialAttentionMessageBrush": "#12AB34",
                "SpecialAttentionUserNameBrush": "#123456"
            } }
            """)!;

        AssertPair(colors.Overrides, "#123456", "#12AB34");
        colors.Overrides.Remove("SpecialAttentionUserNameBrush");
        var restored = JsonSerializer.Deserialize<ColorSchemeSettings>(JsonSerializer.Serialize(colors))!;

        Assert.False(restored.Overrides.ContainsKey("SpecialAttentionUserNameBrush"));
        Assert.Equal("#12AB34", restored.Overrides["SpecialAttentionContentBrush"]);
        Assert.False(restored.Overrides.ContainsKey("SpecialAttentionMessageBrush"));
    }

    /// <summary>磁盘旧配置与旧分享包均通过实际加载入口迁移，新配置保存后仍保留独立颜色。</summary>
    [Fact]
    public async Task ConfigurationAndSharePackagesUpgradeLegacyColors()
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        var source = new PanelSettings();
        source.ColorScheme.Overrides["SpecialAttentionMessageBrush"] = "#12AB34";
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "settings.json"), JsonSerializer.Serialize(source));
        var loaded = store.Load();
        AssertPair(loaded.Settings.ColorScheme.Overrides, "#12AB34", "#12AB34");

        await using var stream = new MemoryStream();
        await JsonSerializer.SerializeAsync(stream, new SettingsSharePackage { Settings = source });
        stream.Position = 0;
        var imported = await new SettingsShareService().ImportAsync(stream);
        AssertPair(imported.ColorScheme.Overrides, "#12AB34", "#12AB34");

        loaded.Settings.ColorScheme.Overrides["SpecialAttentionUserNameBrush"] = "#123456";
        await store.SaveAsync(loaded);
        AssertPair(store.Load().Settings.ColorScheme.Overrides, "#123456", "#12AB34");
    }

    /// <summary>程序内构造的旧配色可直接应用和复制，兼容处理不会修改调用者的颜色字典。</summary>
    [AvaloniaFact]
    public void LegacyInMemoryColorsRenderWithoutMutatingTheSource()
    {
        var source = new ColorSchemeSettings
        {
            Overrides = new() { ["SpecialAttentionMessageBrush"] = "#12AB34" }
        };
        var userName = new TextBlock();
        var content = new TextBlock();
        userName.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("SpecialAttentionUserNameBrush"));
        content.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("SpecialAttentionContentBrush"));
        using var colors = new ColorSchemeService();
        var window = new Window { Content = new StackPanel { Children = { userName, content } } };
        try
        {
            window.Show();
            colors.Apply(Application.Current!, source);
            Assert.Equal(Color.Parse("#12AB34"), Assert.IsAssignableFrom<ISolidColorBrush>(userName.Foreground).Color);
            Assert.Equal(Color.Parse("#12AB34"), Assert.IsAssignableFrom<ISolidColorBrush>(content.Foreground).Color);
            Assert.Equal(Color.Parse("#12AB34"), colors.GetCurrentColor(Application.Current!, source, "SpecialAttentionUserNameBrush"));
            AssertPair(ColorSchemeService.Snapshot(source).Overrides, "#12AB34", "#12AB34");
            Assert.Single(source.Overrides);
            Assert.Equal("#12AB34", source.Overrides["SpecialAttentionMessageBrush"]);
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertPair(Dictionary<string, string> colors, string userName, string content)
    {
        Assert.Equal(userName, colors["SpecialAttentionUserNameBrush"]);
        Assert.Equal(content, colors["SpecialAttentionContentBrush"]);
        Assert.False(colors.ContainsKey("SpecialAttentionMessageBrush"));
    }
}
