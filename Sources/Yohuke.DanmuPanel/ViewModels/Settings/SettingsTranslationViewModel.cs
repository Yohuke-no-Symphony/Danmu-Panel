using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.Views.Settings;

namespace Yohuke.DanmuPanel.ViewModels.Settings;

/// <summary>维护翻译与多语言术语草稿，切换语言前提交当前编辑。</summary>
public sealed partial class SettingsTranslationViewModel : ObservableObject
{
    [ObservableProperty] private bool enabled;
    [ObservableProperty] private string? baseUrl;
    [ObservableProperty] private string? apiKey;
    [ObservableProperty] private string? model;
    [ObservableProperty] private string? extraPrompt;
    [ObservableProperty] private int displayMode;
    [ObservableProperty] private int backlogMode;
    [ObservableProperty] private GlossaryRow? selectedGlossary;
    private TranslationProviderOption selectedProvider;
    private TranslationLanguageOption selectedLanguage;

    public IReadOnlyList<TranslationProviderOption> Providers => TranslationProviderOption.Presets;
    public IReadOnlyList<SettingsChoiceOption> DisplayModeOptions { get; } = SettingsChoiceOption.Create(
        "Settings.Bilingual", "Settings.TranslationOnly");
    public IReadOnlyList<SettingsChoiceOption> BacklogModeOptions { get; } = SettingsChoiceOption.Create(
        "Settings.Realtime", "Settings.AllMessages");
    public IReadOnlyList<TranslationLanguageOption> Languages { get; }
    public ObservableCollection<GlossaryRow> Glossary { get; }
    public IAsyncRelayCommand AddGlossaryTermCommand { get; }
    public IAsyncRelayCommand<GlossaryRow> RemoveGlossaryTermCommand { get; }
    public event Action? CommitEditsRequested;
    public event Action<GlossaryRow>? TermAdded;

    public TranslationProviderOption SelectedProvider
    {
        get => selectedProvider;
        set
        {
            if (SetProperty(ref selectedProvider, value))
            {
                OnPropertyChanged(nameof(IsCustomProvider));
            }
        }
    }

    public TranslationLanguageOption SelectedLanguage
    {
        get => selectedLanguage;
        set
        {
            if (selectedLanguage == value)
            {
                return;
            }
            CommitEditsRequested?.Invoke();
            if (SetProperty(ref selectedLanguage, value))
            {
                foreach (var term in Glossary)
                {
                    term.SelectLanguage(value.Code);
                }
                OnPropertyChanged(nameof(GlossaryLanguage));
            }
        }
    }

    public bool IsCustomProvider => SelectedProvider.BaseUrl is null;
    public string SelectedBaseUrl => SelectedProvider.BaseUrl ?? BaseUrl?.Trim() ?? "";
    public string GlossaryLanguage => $"({SelectedLanguage.Code})";

    /// <summary>深复制翻译设置，并保留配置中的自定义目标语言。</summary>
    public SettingsTranslationViewModel(TranslationSettings settings)
    {
        var snapshot = TranslationRequestFactory.Snapshot(settings);
        enabled = snapshot.Enabled;
        baseUrl = snapshot.BaseUrl;
        apiKey = snapshot.ApiKey;
        model = snapshot.Model;
        extraPrompt = snapshot.ExtraSystemPrompt;
        displayMode = (int)snapshot.DisplayMode;
        backlogMode = (int)snapshot.BacklogMode;
        selectedProvider = Providers.FirstOrDefault(option => string.Equals(option.BaseUrl,
            snapshot.BaseUrl.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) ?? Providers[^1];
        var languages = TranslationLanguageOption.Presets.ToList();
        selectedLanguage = languages.FirstOrDefault(option => option.Code == snapshot.TargetLanguage) ?? new(snapshot.TargetLanguage);
        if (!languages.Contains(selectedLanguage))
        {
            languages.Add(selectedLanguage);
        }
        Languages = languages;
        Glossary = new(snapshot.Glossary.GroupBy(entry => entry.Source.Trim(), StringComparer.Ordinal)
            .Select(entries => new GlossaryRow(entries.Key, selectedLanguage.Code, entries)));
        AddGlossaryTermCommand = new AsyncRelayCommand(AddTermAsync);
        RemoveGlossaryTermCommand = new AsyncRelayCommand<GlossaryRow>(RemoveTermAsync, term => term is not null || SelectedGlossary is not null);
    }

    /// <summary>更新下拉选项的本地化显示，不重建草稿或选中对象。</summary>
    public void RefreshLocalization()
    {
        foreach (var option in DisplayModeOptions.Concat(BacklogModeOptions))
        {
            option.RefreshLocalization();
        }
        foreach (var language in Languages)
        {
            language.RefreshLocalization();
        }
    }

    partial void OnSelectedGlossaryChanged(GlossaryRow? value)
    {
        RemoveGlossaryTermCommand.NotifyCanExecuteChanged();
    }

    private Task AddTermAsync()
    {
        CommitEditsRequested?.Invoke();
        var term = new GlossaryRow("", SelectedLanguage.Code, []);
        Glossary.Add(term);
        SelectedGlossary = term;
        TermAdded?.Invoke(term);
        return Task.CompletedTask;
    }

    private Task RemoveTermAsync(GlossaryRow? term)
    {
        term ??= SelectedGlossary;
        if (term is not null)
        {
            CommitEditsRequested?.Invoke();
            Glossary.Remove(term);
        }
        return Task.CompletedTask;
    }
}
