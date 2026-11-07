using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Lucide.Avalonia;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.Appearance;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services.Appearance;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Views.Settings;

/// <summary>编辑全局颜色覆盖并管理命名方案，保留尚未完成的颜色输入。</summary>
public sealed class ColorSchemeEditor : UserControl
{
    private readonly PanelSettings settings;
    private readonly ColorSchemeService colors;
    private readonly Dictionary<string, ColorInput> inputs = new(StringComparer.Ordinal);
    private readonly ComboBox schemes;
    private readonly TextBox schemeName;
    private readonly TextBlock status;
    private readonly Button update;
    private readonly Button delete;
    private bool updating;

    /// <summary>有效颜色、方案或主题发生变化，宿主应更新全局预览。</summary>
    public event Action? Changed;

    /// <summary>创建针对现有全局设置的颜色编辑器，保存由设置宿主统一执行。</summary>
    public ColorSchemeEditor(PanelSettings settings, ColorSchemeService colors, IReadOnlySet<string>? detachedColorKeys = null)
    {
        this.settings = settings;
        this.colors = colors;
        Name = "ColorSchemeEditor";
        schemes = new()
        {
            Name = "ColorSchemeSelector", HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = new FuncDataTemplate<SavedColorScheme>((scheme, _) => scheme is null
                ? new TextBlock()
                : scheme.Id.Length == 0
                    ? Label(settings.ColorScheme.Overrides.Count == 0 ? "Colors.DefaultScheme" : "Colors.CustomScheme")
                    : new TextBlock { Text = scheme.Name })
        };
        schemeName = new() { Name = "ColorSchemeName", HorizontalAlignment = HorizontalAlignment.Stretch };
        schemeName.Bind(TextBox.WatermarkProperty, Localization.Observe("Colors.SchemeName"));
        var saveAs = ActionButton("Colors.SaveAs", "SaveColorSchemeAs", LucideIconKind.CopyPlus, SaveAs);
        update = ActionButton("Colors.UpdateScheme", "UpdateColorScheme", LucideIconKind.Save, UpdateScheme);
        delete = ActionButton("Colors.DeleteScheme", "DeleteColorScheme", LucideIconKind.Trash2, DeleteScheme);
        var reset = ActionButton("Colors.ResetAll", "ResetAllColors", LucideIconKind.RotateCcw, ResetAll);
        status = new() { Name = "ColorSchemeStatus", TextWrapping = TextWrapping.Wrap, IsVisible = false };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { saveAs, update, delete, reset }
        };
        var heading = Label("Colors.Title");
        heading.Classes.Add("sectionTitle");
        heading.VerticalAlignment = VerticalAlignment.Center;
        var header = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 };
        header.Children.Add(heading);
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);

        var content = new StackPanel
        {
            Spacing = 12,
            Children = { header, Row("Colors.Scheme", schemes), Row("Colors.SchemeName", schemeName), status }
        };
        foreach (var group in ColorSettingCatalog.Definitions.GroupBy(definition => definition.GroupKey))
        {
            var rows = new StackPanel { Spacing = 10, Margin = new Thickness(0, 8, 0, 0) };
            foreach (var definition in group)
            {
                var input = new ColorInput { Name = "ColorInput_" + definition.Key };
                input.ValueChanged += value => OnColorChanged(definition.Key, value);
                inputs.Add(definition.Key, input);
                if (detachedColorKeys?.Contains(definition.Key) != true)
                {
                    rows.Children.Add(Row(definition.NameKey, input));
                }
            }
            var groupTitle = Label(group.Key);
            groupTitle.Classes.Add("sectionTitle");
            content.Children.Add(new StackPanel
            {
                Name = group.Key.Replace('.', '_'), Spacing = 2,
                Margin = new Thickness(0, 8, 0, 0),
                Children = { groupTitle, rows }
            });
        }
        var frame = new Border { Padding = new Thickness(14), Child = content };
        frame.Classes.Add("panel");
        Content = frame;
        LoadInputs();
        RefreshSchemes(true);
        schemes.SelectionChanged += (_, _) => SelectScheme();
        ActualThemeVariantChanged += (_, _) => RefreshDefaults();
    }

    /// <summary>校验所有颜色草稿，失败时保留原始输入并定位错误字段。</summary>
    public bool TryCommit(out string? error)
    {
        var overrides = new Dictionary<string, string>(settings.ColorScheme.Overrides, StringComparer.Ordinal);
        foreach (var (key, input) in inputs)
        {
            if (!input.TryRead(out var value))
            {
                error = Localization.Get("Colors.InvalidColor");
                input.FocusInput();
                ShowStatus(error);
                return false;
            }
            if (value is null)
            {
                overrides.Remove(key);
            }
            else
            {
                overrides[key] = value;
            }
        }
        try
        {
            ColorSchemeService.Validate(settings.ColorScheme);
        }
        catch (InvalidDataException)
        {
            error = Localization.Get("Colors.InvalidColor");
            ShowStatus(error);
            return false;
        }
        if (overrides.Count != settings.ColorScheme.Overrides.Count || overrides.Any(pair =>
            !settings.ColorScheme.Overrides.TryGetValue(pair.Key, out var value) || value != pair.Value))
        {
            settings.ColorScheme.Overrides = overrides;
            Changed?.Invoke();
        }
        error = null;
        return true;
    }

    /// <summary>主题切换后刷新默认色块，不覆盖未完成的颜色或方案名称草稿。</summary>
    public void RefreshDefaults()
    {
        foreach (var (key, input) in inputs)
        {
            input.RefreshDefault(colors.GetDefaultColor(Application.Current!, key));
        }
    }

    internal ColorInput GetInput(string key)
    {
        return inputs[key];
    }

    private void OnColorChanged(string key, string? value)
    {
        if (updating)
        {
            return;
        }
        var overrides = new Dictionary<string, string>(settings.ColorScheme.Overrides, StringComparer.Ordinal);
        if (value is null)
        {
            overrides.Remove(key);
        }
        else
        {
            overrides[key] = value;
        }
        settings.ColorScheme.Overrides = overrides;
        RefreshSchemes(false);
        ShowStatus(null);
        Changed?.Invoke();
    }

    private void SelectScheme()
    {
        if (updating || schemes.SelectedItem is not SavedColorScheme selected)
        {
            return;
        }
        if (!TryCommit(out _))
        {
            RefreshSchemes(false);
            return;
        }
        settings.ColorScheme.SelectedSchemeId = selected.Id.Length == 0 ? null : selected.Id;
        settings.ColorScheme.Overrides = new(selected.Colors, StringComparer.Ordinal);
        if (selected.Id.Length > 0)
        {
            settings.Theme = selected.Theme;
        }
        Changed?.Invoke();
        LoadInputs();
        RefreshSchemes(true);
        ShowStatus(null);
    }

    private void SaveAs()
    {
        if (!TryCommit(out _) || !TryReadName(null, out var name))
        {
            return;
        }
        var scheme = new SavedColorScheme
        {
            Id = Guid.NewGuid().ToString("N"), Name = name, Theme = settings.Theme,
            Colors = new(settings.ColorScheme.Overrides, StringComparer.Ordinal)
        };
        settings.ColorScheme.SavedSchemes.Add(scheme);
        settings.ColorScheme.SelectedSchemeId = scheme.Id;
        RefreshSchemes(true);
        ShowStatus(Localization.Get("Colors.SchemeSaved"));
        Changed?.Invoke();
    }

    private void UpdateScheme()
    {
        var selected = GetSelectedScheme();
        if (selected is null || !TryCommit(out _) || !TryReadName(selected.Id, out var name))
        {
            return;
        }
        selected.Name = name;
        selected.Theme = settings.Theme;
        selected.Colors = new(settings.ColorScheme.Overrides, StringComparer.Ordinal);
        RefreshSchemes(true);
        ShowStatus(Localization.Get("Colors.SchemeSaved"));
        Changed?.Invoke();
    }

    private void DeleteScheme()
    {
        var selected = GetSelectedScheme();
        if (selected is null)
        {
            return;
        }
        settings.ColorScheme.SavedSchemes.Remove(selected);
        settings.ColorScheme.SelectedSchemeId = null;
        RefreshSchemes(true);
        ShowStatus(Localization.Get("Colors.SchemeDeleted"));
        Changed?.Invoke();
    }

    private void ResetAll()
    {
        settings.ColorScheme.Overrides = new(StringComparer.Ordinal);
        settings.ColorScheme.SelectedSchemeId = null;
        Changed?.Invoke();
        LoadInputs();
        RefreshSchemes(true);
        ShowStatus(null);
    }

    private bool TryReadName(string? currentId, out string name)
    {
        name = schemeName.Text?.Trim() ?? "";
        if (name.Length == 0)
        {
            ShowStatus(Localization.Get("Colors.NameRequired"));
            schemeName.Focus();
            return false;
        }
        var candidate = name;
        if (settings.ColorScheme.SavedSchemes.Any(scheme => scheme.Id != currentId &&
            string.Equals(scheme.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            ShowStatus(Localization.Get("Colors.NameExists"));
            schemeName.Focus();
            return false;
        }
        return true;
    }

    private SavedColorScheme? GetSelectedScheme()
    {
        return settings.ColorScheme.SavedSchemes.FirstOrDefault(scheme => scheme.Id == settings.ColorScheme.SelectedSchemeId);
    }

    private void LoadInputs()
    {
        foreach (var (key, input) in inputs)
        {
            settings.ColorScheme.Overrides.TryGetValue(key, out var value);
            input.Load(value, colors.GetDefaultColor(Application.Current!, key));
        }
    }

    private void RefreshSchemes(bool refreshName)
    {
        updating = true;
        try
        {
            var choices = new List<SavedColorScheme>
            {
                new()
                {
                    Id = "", Theme = settings.Theme,
                    Colors = new(settings.ColorScheme.Overrides, StringComparer.Ordinal)
                }
            };
            choices.AddRange(settings.ColorScheme.SavedSchemes);
            var selected = GetSelectedScheme();
            schemes.ItemsSource = choices;
            schemes.SelectedItem = selected ?? choices[0];
            update.IsEnabled = selected is not null;
            delete.IsEnabled = selected is not null;
            if (refreshName)
            {
                schemeName.Text = selected?.Name ?? "";
            }
        }
        finally
        {
            updating = false;
        }
    }

    private void ShowStatus(string? text)
    {
        status.Text = text;
        status.IsVisible = !string.IsNullOrEmpty(text);
    }

    private static TextBlock Label(string key)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        text.Bind(TextBlock.TextProperty, Localization.Observe(key));
        return text;
    }

    private static Control Row(string key, Control value)
    {
        var row = new Grid { ColumnDefinitions = new("*,280"), ColumnSpacing = 14 };
        var label = Label(key);
        label.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(label);
        Grid.SetColumn(value, 1);
        row.Children.Add(value);
        return row;
    }

    private static Button ActionButton(string key, string name, LucideIconKind icon, Action action)
    {
        var button = new Button
        {
            Name = name,
            Content = new LucideIcon { Kind = icon, Size = 16 },
            VerticalAlignment = VerticalAlignment.Center
        };
        button.Classes.Add("iconButton");
        button.Bind(ToolTip.TipProperty, Localization.Observe(key));
        button.Bind(AutomationProperties.NameProperty, Localization.Observe(key));
        button.Click += (_, _) => action();
        return button;
    }
}
