using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Yohuke.DanmuPanel.Models.Appearance;

namespace Yohuke.DanmuPanel.Services.Appearance;

/// <summary>验证个人配色并通过独立资源字典应用覆盖，释放时恢复原主题。</summary>
public sealed class ColorSchemeService : IDisposable
{
    private readonly ResourceDictionary resources = new();
    private readonly Dictionary<(ThemeVariant Theme, string Key), Color?> defaults = new();
    private readonly Dictionary<ThemeVariant, (ColorPaletteResources? Palette, Color Accent)> originalPalettes = new();
    private Application? application;
    private FluentTheme? fluent;

    /// <summary>应用全部有效覆盖；空覆盖恢复原主题和 Fluent 强调色。</summary>
    public void Apply(Application target, ColorSchemeSettings settings)
    {
        Validate(settings);
        if (settings.Overrides.ContainsKey(ColorSchemeSettings.LEGACY_SPECIAL_ATTENTION_MESSAGE_BRUSH))
        {
            settings = Snapshot(settings);
        }
        Attach(target);
        resources.Clear();
        foreach (var (key, value) in settings.Overrides)
        {
            resources[key] = new SolidColorBrush(Color.Parse(value));
        }

        if (settings.Overrides.TryGetValue("AppPrimaryBrush", out var accentText))
        {
            var accent = Color.Parse(accentText);
            resources["SystemAccentColor"] = accent;
            ApplyAccent(accent);
        }
        else
        {
            RestoreAccent();
        }

        if (resources.Count > 0 && !target.Resources.MergedDictionaries.Contains(resources))
        {
            target.Resources.MergedDictionaries.Add(resources);
        }
        else if (resources.Count == 0)
        {
            target.Resources.MergedDictionaries.Remove(resources);
        }
    }

    /// <summary>读取当前深浅主题的原始颜色；SC 默认沿用每条消息颜色，返回空值。</summary>
    public Color? GetDefaultColor(Application target, string key)
    {
        Attach(target);
        var theme = target.ActualThemeVariant == ThemeVariant.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
        return defaults[(theme, ColorSettingCatalog.Get(key).Key)];
    }

    /// <summary>读取当前主题下用户实际选择的颜色。</summary>
    public Color? GetCurrentColor(Application target, ColorSchemeSettings settings, string key)
    {
        ColorSettingCatalog.Get(key);
        if (settings.Overrides.ContainsKey(ColorSchemeSettings.LEGACY_SPECIAL_ATTENTION_MESSAGE_BRUSH))
        {
            settings = Snapshot(settings);
        }
        return settings.Overrides.TryGetValue(key, out var color) ? Color.Parse(color) : GetDefaultColor(target, key);
    }

    /// <summary>深复制全部当前颜色与已保存方案，草稿不会修改原始设置。</summary>
    public static ColorSchemeSettings Snapshot(ColorSchemeSettings settings)
    {
        var snapshot = new ColorSchemeSettings
        {
            Overrides = new(settings.Overrides, StringComparer.Ordinal),
            SelectedSchemeId = settings.SelectedSchemeId,
            SavedSchemes = settings.SavedSchemes.Select(scheme => new SavedColorScheme
            {
                Id = scheme.Id,
                Name = scheme.Name,
                Theme = scheme.Theme,
                Colors = new(scheme.Colors, StringComparer.Ordinal)
            }).ToList()
        };
        snapshot.UpgradeLegacyAttentionColors();
        return snapshot;
    }

    /// <summary>在加载或提交边界验证方案结构与颜色，拒绝无法应用的配置。</summary>
    public static void Validate(ColorSchemeSettings settings)
    {
        if (settings is null || settings.Overrides is null || settings.SavedSchemes is null)
        {
            throw new InvalidDataException("Color scheme settings are missing.");
        }
        ValidateColors(settings.Overrides);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var scheme in settings.SavedSchemes)
        {
            if (scheme is null || string.IsNullOrWhiteSpace(scheme.Id) || string.IsNullOrWhiteSpace(scheme.Name) ||
                scheme.Theme is not ("system" or "light" or "dark") || scheme.Colors is null ||
                !ids.Add(scheme.Id) || !names.Add(scheme.Name.Trim()))
            {
                throw new InvalidDataException("A saved color scheme is invalid or duplicated.");
            }
            ValidateColors(scheme.Colors);
        }
        if (settings.SelectedSchemeId is not null && !ids.Contains(settings.SelectedSchemeId))
        {
            throw new InvalidDataException("The selected color scheme does not exist.");
        }
    }

    /// <summary>撤回此服务的资源与强调色覆盖，不修改应用的其他资源。</summary>
    public void Dispose()
    {
        application?.Resources.MergedDictionaries.Remove(resources);
        RestoreAccent();
        resources.Clear();
        defaults.Clear();
        application = null;
        fluent = null;
    }

    private void Attach(Application target)
    {
        if (ReferenceEquals(application, target))
        {
            return;
        }
        Dispose();
        application = target;
        fluent = target.Styles.OfType<FluentTheme>().FirstOrDefault();
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            foreach (var definition in ColorSettingCatalog.Definitions)
            {
                defaults[(theme, definition.Key)] = ReadDefault(target, theme, definition);
            }
        }
    }

    private static Color? ReadDefault(Application target, ThemeVariant theme, ColorSettingDefinition definition)
    {
        if (definition.Key == "SuperChatBorderBrush")
        {
            return null;
        }
        if (!target.Styles.TryGetResource(definition.Key, theme, out var value) && definition.DefaultResourceKey is { } fallback)
        {
            target.Styles.TryGetResource(fallback, theme, out value);
        }
        return value switch
        {
            ISolidColorBrush brush => Color.FromArgb((byte)Math.Round(brush.Color.A * brush.Opacity), brush.Color.R, brush.Color.G, brush.Color.B),
            Color color => color,
            _ => definition.DefaultColor is { } text ? Color.Parse(text) : null
        };
    }

    private void ApplyAccent(Color accent)
    {
        if (fluent is null)
        {
            return;
        }
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            if (!originalPalettes.ContainsKey(theme))
            {
                originalPalettes[theme] = fluent.Palettes.TryGetValue(theme, out var original)
                    ? (original, original.Accent) : (null, default);
            }
            if (!fluent.Palettes.TryGetValue(theme, out var palette))
            {
                palette = new();
                fluent.Palettes[theme] = palette;
            }
            palette.Accent = accent;
        }
    }

    private void RestoreAccent()
    {
        if (fluent is not null)
        {
            foreach (var (theme, original) in originalPalettes)
            {
                if (original.Palette is null)
                {
                    fluent.Palettes.Remove(theme);
                }
                else
                {
                    original.Palette.Accent = original.Accent;
                    fluent.Palettes[theme] = original.Palette;
                }
            }
        }
        originalPalettes.Clear();
    }

    private static void ValidateColors(Dictionary<string, string> colors)
    {
        foreach (var (key, value) in colors)
        {
            if (!ColorSettingCatalog.Contains(key) || string.IsNullOrWhiteSpace(value) || !Color.TryParse(value, out _))
            {
                throw new InvalidDataException($"Invalid color setting: {key}.");
            }
        }
    }
}
