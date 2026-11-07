using CommunityToolkit.Mvvm.ComponentModel;
using Yohuke.DanmuPanel.Models.Translation;

namespace Yohuke.DanmuPanel.Views.Settings;

/// <summary>在同一原词行中编辑多种目标语言的译法。</summary>
public sealed class GlossaryRow : ObservableObject
{
    private readonly List<GlossaryEntry> translations;
    private string source;
    private string targetLanguage;

    public string Source
    {
        get => source;
        set => SetProperty(ref source, value);
    }

    public string Target
    {
        get => translations.FirstOrDefault(IsSelectedLanguage)?.Target ?? "";
        set
        {
            var entries = translations.Where(IsSelectedLanguage).ToArray();
            if (entries.Length == 0)
            {
                translations.Add(new() { Target = value, TargetLanguage = targetLanguage });
            }
            else
            {
                foreach (var entry in entries)
                {
                    entry.Target = value;
                }
            }
            OnPropertyChanged();
        }
    }

    internal bool HasTranslations => translations.Any(entry => !string.IsNullOrWhiteSpace(entry.Target));

    internal GlossaryRow(string source, string targetLanguage, IEnumerable<GlossaryEntry> translations)
    {
        this.source = source;
        this.targetLanguage = targetLanguage;
        this.translations = translations.ToList();
    }

    internal void SelectLanguage(string language)
    {
        targetLanguage = language;
        OnPropertyChanged(nameof(Target));
    }

    internal IEnumerable<GlossaryEntry> GetEntries()
    {
        return translations.Where(entry => !string.IsNullOrWhiteSpace(entry.Target)).Select(entry => new GlossaryEntry
        {
            Source = Source.Trim(),
            Target = entry.Target.Trim(),
            TargetLanguage = entry.TargetLanguage.Trim()
        });
    }

    private bool IsSelectedLanguage(GlossaryEntry entry)
    {
        return string.Equals(entry.TargetLanguage.Trim(), targetLanguage.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
