using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Xunit;
using Yohuke.DanmuPanel.Models.Appearance;
using Yohuke.DanmuPanel.Services.Appearance;

namespace Yohuke.DanmuPanel.Tests.Appearance;

/// <summary>验证配色快照、持久化边界和动态资源恢复。</summary>
public sealed class ColorSchemeServiceTests
{
    /// <summary>旧设置的空配色保持默认，方案序列化可保留主题和透明度。</summary>
    [Fact]
    public void ColorSchemesRoundTripAndDraftCopiesAreIndependent()
    {
        var settings = new ColorSchemeSettings
        {
            Overrides = new() { ["MessageTranslationBrush"] = "#80445566" },
            SavedSchemes =
            [
                new() { Id = "night", Name = "夜间", Theme = "dark", Colors = new() { ["MessageTranslationBrush"] = "#80445566" } }
            ],
            SelectedSchemeId = "night"
        };
        ColorSchemeService.Validate(new());
        var copy = ColorSchemeService.Snapshot(settings);
        copy.Overrides["MessageTranslationBrush"] = "#112233";
        copy.SavedSchemes[0].Colors["MessageTranslationBrush"] = "#667788";
        copy.SavedSchemes[0].Name = "改名";
        Assert.Equal("#80445566", settings.Overrides["MessageTranslationBrush"]);
        Assert.Equal("#80445566", settings.SavedSchemes[0].Colors["MessageTranslationBrush"]);
        Assert.Equal("夜间", settings.SavedSchemes[0].Name);

        var restored = JsonSerializer.Deserialize<ColorSchemeSettings>(JsonSerializer.Serialize(settings))!;
        ColorSchemeService.Validate(restored);
        Assert.Equal("night", restored.SelectedSchemeId);
        Assert.Equal("dark", Assert.Single(restored.SavedSchemes).Theme);
        Assert.Equal("#80445566", restored.Overrides["MessageTranslationBrush"]);
    }

    /// <summary>无效资源键或色值在更改运行中的资源前被拒绝。</summary>
    [Theory]
    [InlineData("UnknownBrush", "#123456")]
    [InlineData("AppTextBrush", "#invalid")]
    [InlineData("AppTextBrush", "")]
    public void InvalidColorOverridesAreRejected(string key, string value)
    {
        Assert.Throws<InvalidDataException>(() => ColorSchemeService.Validate(new()
        {
            Overrides = new() { [key] = value }
        }));
    }

    /// <summary>缺失引用和重复方案不能静默覆盖已保存方案。</summary>
    [Fact]
    public void BrokenSchemeReferencesAndDuplicatesAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => ColorSchemeService.Validate(new() { SelectedSchemeId = "missing" }));
        Assert.Throws<InvalidDataException>(() => ColorSchemeService.Validate(new()
        {
            SavedSchemes = [new() { Id = "same", Name = "first" }, new() { Id = "same", Name = "second" }]
        }));
        Assert.Throws<InvalidDataException>(() => ColorSchemeService.Validate(new()
        {
            SavedSchemes = [new() { Name = "night" }, new() { Name = " NIGHT " }]
        }));
        Assert.Throws<InvalidDataException>(() => ColorSchemeService.Validate(new()
        {
            SavedSchemes = [new() { Name = "scheme", Theme = "invalid" }]
        }));
    }

    /// <summary>已显示控件即时收到颜色覆盖；撤销后恢复主题并保留其他资源字典。</summary>
    [AvaloniaFact]
    public void ExistingDynamicResourceBindingsUpdateAndRestore()
    {
        var application = Application.Current!;
        using var service = new ColorSchemeService();
        var existingCount = application.Resources.MergedDictionaries.Count;
        var text = new TextBlock { Text = "配色预览" };
        text.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextBrush"));
        var window = new Window { Content = text };
        try
        {
            window.Show();
            var original = Assert.IsAssignableFrom<ISolidColorBrush>(text.Foreground).Color;
            service.Apply(application, new() { Overrides = new() { ["AppTextBrush"] = "#80445566" } });
            Assert.Equal(Color.Parse("#80445566"), Assert.IsAssignableFrom<ISolidColorBrush>(text.Foreground).Color);
            Assert.Equal(existingCount + 1, application.Resources.MergedDictionaries.Count);
            Assert.Equal(original, service.GetDefaultColor(application, "AppTextBrush"));

            Assert.Throws<InvalidDataException>(() => service.Apply(application,
                new() { Overrides = new() { ["AppTextBrush"] = "not-a-color" } }));
            Assert.Equal(Color.Parse("#80445566"), Assert.IsAssignableFrom<ISolidColorBrush>(text.Foreground).Color);

            service.Apply(application, new());
            Assert.Equal(original, Assert.IsAssignableFrom<ISolidColorBrush>(text.Foreground).Color);
            Assert.Equal(existingCount, application.Resources.MergedDictionaries.Count);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>浅色和深色默认值独立解析，SC 保留按消息着色。</summary>
    [AvaloniaFact]
    public void DefaultsFollowThemeAndSuperChatHasNoGlobalDefault()
    {
        var application = Application.Current!;
        var originalTheme = application.RequestedThemeVariant;
        using var service = new ColorSchemeService();
        try
        {
            application.RequestedThemeVariant = ThemeVariant.Light;
            var light = service.GetCurrentColor(application, new(), "AppTextBrush");
            application.RequestedThemeVariant = ThemeVariant.Dark;
            var dark = service.GetCurrentColor(application, new(), "AppTextBrush");
            Assert.NotEqual(light, dark);
            Assert.Null(service.GetDefaultColor(application, "SuperChatBorderBrush"));
            Assert.Equal(Color.Parse("#80445566"), service.GetCurrentColor(application,
                new() { Overrides = new() { ["SuperChatBorderBrush"] = "#80445566" } }, "SuperChatBorderBrush"));
        }
        finally
        {
            application.RequestedThemeVariant = originalTheme;
        }
    }

    /// <summary>Fluent 控件的两种主题强调色同步更新，释放时恢复原对象和颜色。</summary>
    [AvaloniaFact]
    public void AccentUpdatesBothFluentPalettesAndDisposeRestoresThem()
    {
        var application = Application.Current!;
        var fluent = application.Styles.OfType<FluentTheme>().Single();
        var originals = new[] { ThemeVariant.Light, ThemeVariant.Dark }.ToDictionary(theme => theme, theme =>
        {
            fluent.Palettes.TryGetValue(theme, out var palette);
            return (Palette: palette, Accent: palette?.Accent);
        });
        var existingCount = application.Resources.MergedDictionaries.Count;
        using (var service = new ColorSchemeService())
        {
            service.Apply(application, new() { Overrides = new() { ["AppPrimaryBrush"] = "#AA44CC" } });
            Assert.All(fluent.Palettes.Values, palette => Assert.Equal(Color.Parse("#AA44CC"), palette.Accent));
            service.Apply(application, new() { Overrides = new() { ["AppPrimaryBrush"] = "#22AA44" } });
            Assert.All(fluent.Palettes.Values, palette => Assert.Equal(Color.Parse("#22AA44"), palette.Accent));
        }
        foreach (var (theme, original) in originals)
        {
            if (original.Palette is null)
            {
                Assert.False(fluent.Palettes.ContainsKey(theme));
            }
            else
            {
                Assert.Same(original.Palette, fluent.Palettes[theme]);
                Assert.Equal(original.Accent, fluent.Palettes[theme].Accent);
            }
        }
        Assert.Equal(existingCount, application.Resources.MergedDictionaries.Count);
    }
}
