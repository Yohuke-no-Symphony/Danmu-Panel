using Avalonia;
using Avalonia.Controls;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services.Appearance;
using Yohuke.DanmuPanel.ViewModels.Settings;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Views.Settings;

/// <summary>编辑全局颜色覆盖并管理命名方案，保留尚未完成的颜色输入。</summary>
public sealed class ColorSchemeEditor : UserControl
{
    private readonly Dictionary<string, ColorInput> inputs = new(StringComparer.Ordinal);

    public ColorSchemeEditorViewModel Model { get; }
    public IReadOnlyList<ColorSettingGroupPresentation> Groups { get; }

    /// <summary>有效颜色、方案或主题发生变化，宿主应更新全局预览。</summary>
    public event Action? Changed;

    /// <summary>创建针对现有全局设置的颜色编辑器，保存由设置宿主统一执行。</summary>
    public ColorSchemeEditor(PanelSettings settings, ColorSchemeService colors, IReadOnlySet<string>? detachedColorKeys = null)
    {
        Name = "ColorSchemeEditor";
        Model = new(settings, key => colors.GetDefaultColor(Application.Current!, key));
        foreach (var definition in ColorSettingCatalog.Definitions)
        {
            inputs.Add(definition.Key, new() { Name = "ColorInput_" + definition.Key, Model = Model.Inputs[definition.Key] });
        }
        Groups = ColorSettingCatalog.Definitions.GroupBy(definition => definition.GroupKey)
            .Select(group => new ColorSettingGroupPresentation(group.Key, group
                .Where(definition => detachedColorKeys?.Contains(definition.Key) != true)
                .Select(definition => new ColorSettingRowPresentation(definition, inputs[definition.Key])).ToArray())).ToArray();
        var surface = new ColorSchemeEditorSurface { DataContext = this };
        Content = surface;
        Model.Changed += () => Changed?.Invoke();
        Model.FocusInputRequested += key => inputs[key].FocusInput();
        Model.NameFocusRequested += () => surface.FindControl<TextBox>("ColorSchemeName")!.Focus();
        ActualThemeVariantChanged += (_, _) => RefreshDefaults();
    }

    /// <summary>校验所有颜色草稿，失败时保留原始输入并定位错误字段。</summary>
    public bool TryCommit(out string? error)
    {
        return Model.TryCommit(out error);
    }

    /// <summary>主题切换后刷新默认色块，不覆盖未完成的颜色或方案名称草稿。</summary>
    public void RefreshDefaults()
    {
        Model.RefreshDefaults();
    }

    internal ColorInput GetInput(string key)
    {
        return inputs[key];
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs args)
    {
        base.OnAttachedToVisualTree(args);
        Localization.LanguageChanged += OnLanguageChanged;
        RefreshLocalization();
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs args)
    {
        Localization.LanguageChanged -= OnLanguageChanged;
        base.OnDetachedFromVisualTree(args);
    }

    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        RefreshLocalization();
    }

    private void RefreshLocalization()
    {
        Model.RefreshLocalization();
        foreach (var group in Groups)
        {
            group.RefreshLocalization();
        }
    }
}
