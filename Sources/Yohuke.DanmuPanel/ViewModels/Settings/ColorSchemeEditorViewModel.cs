using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.Appearance;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services.Appearance;

namespace Yohuke.DanmuPanel.ViewModels.Settings;

/// <summary>管理颜色草稿、命名方案快照和方案命令，不持有界面控件。</summary>
public sealed class ColorSchemeEditorViewModel : ObservableObject
{
    private readonly PanelSettings settings;
    private readonly Func<string, Color?> getDefaultColor;
    private readonly Dictionary<string, ColorInputViewModel> inputs = new(StringComparer.Ordinal);
    private IReadOnlyList<SavedColorScheme> schemes = [];
    private SavedColorScheme? selectedScheme;
    private string schemeName = "";
    private string status = "";
    private bool updating;

    public IReadOnlyDictionary<string, ColorInputViewModel> Inputs => inputs;
    public IReadOnlyList<SavedColorScheme> Schemes
    {
        get => schemes;
        private set => SetProperty(ref schemes, value);
    }

    public SavedColorScheme? SelectedScheme
    {
        get => selectedScheme;
        set
        {
            if (SetProperty(ref selectedScheme, value) && !updating)
            {
                SelectScheme();
            }
        }
    }

    public string SchemeName
    {
        get => schemeName;
        set => SetProperty(ref schemeName, value ?? "");
    }

    public string Status
    {
        get => status;
        private set
        {
            if (SetProperty(ref status, value))
            {
                OnPropertyChanged(nameof(HasStatus));
            }
        }
    }

    public bool HasStatus => Status.Length > 0;
    public IRelayCommand SaveAsCommand { get; }
    public IRelayCommand UpdateCommand { get; }
    public IRelayCommand DeleteCommand { get; }
    public IRelayCommand ResetCommand { get; }

    /// <summary>有效颜色、方案或主题变化，通知宿主刷新全局预览。</summary>
    public event Action? Changed;
    /// <summary>请求视图定位第一条无效颜色。</summary>
    public event Action<string>? FocusInputRequested;
    /// <summary>请求视图聚焦方案名称。</summary>
    public event Action? NameFocusRequested;

    /// <summary>复制当前颜色配置并建立每个颜色的独立草稿。</summary>
    public ColorSchemeEditorViewModel(PanelSettings settings, Func<string, Color?> getDefaultColor)
    {
        settings.ColorScheme = ColorSchemeService.Snapshot(settings.ColorScheme);
        this.settings = settings;
        this.getDefaultColor = getDefaultColor;
        foreach (var definition in ColorSettingCatalog.Definitions)
        {
            var input = new ColorInputViewModel();
            input.ValueChanged += value => OnColorChanged(definition.Key, value);
            inputs.Add(definition.Key, input);
        }
        SaveAsCommand = new RelayCommand(SaveAs);
        UpdateCommand = new RelayCommand(UpdateScheme, () => GetSelectedScheme() is not null);
        DeleteCommand = new RelayCommand(DeleteScheme, () => GetSelectedScheme() is not null);
        ResetCommand = new RelayCommand(ResetAll);
        LoadInputs();
        RefreshSchemes(true);
    }

    /// <summary>刷新方案显示名称，不覆盖方案名称和颜色草稿。</summary>
    public void RefreshLocalization()
    {
        RefreshSchemes(false);
        foreach (var input in inputs.Values)
        {
            input.RefreshLocalization();
        }
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
                FocusInputRequested?.Invoke(key);
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
            input.RefreshDefault(getDefaultColor(key));
        }
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
        if (updating || SelectedScheme is not { } selected)
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
        name = SchemeName.Trim();
        if (name.Length == 0)
        {
            ShowStatus(Localization.Get("Colors.NameRequired"));
            NameFocusRequested?.Invoke();
            return false;
        }
        var candidate = name;
        if (settings.ColorScheme.SavedSchemes.Any(scheme => scheme.Id != currentId &&
            string.Equals(scheme.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            ShowStatus(Localization.Get("Colors.NameExists"));
            NameFocusRequested?.Invoke();
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
            input.Load(value, getDefaultColor(key));
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
                    Name = Localization.Get(settings.ColorScheme.Overrides.Count == 0 ? "Colors.DefaultScheme" : "Colors.CustomScheme"),
                    Colors = new(settings.ColorScheme.Overrides, StringComparer.Ordinal)
                }
            };
            choices.AddRange(settings.ColorScheme.SavedSchemes);
            var selected = GetSelectedScheme();
            Schemes = choices;
            SelectedScheme = selected ?? choices[0];
            UpdateCommand.NotifyCanExecuteChanged();
            DeleteCommand.NotifyCanExecuteChanged();
            if (refreshName)
            {
                SchemeName = selected?.Name ?? "";
            }
        }
        finally
        {
            updating = false;
        }
    }

    private void ShowStatus(string? text)
    {
        Status = text ?? "";
    }

}
