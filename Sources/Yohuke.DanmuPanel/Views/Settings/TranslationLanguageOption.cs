using System.Globalization;
using System.ComponentModel;
using Yohuke.DanmuPanel.I18n;

namespace Yohuke.DanmuPanel.Views.Settings;

/// <summary>翻译目标语言的代码与跟随界面语言刷新的显示名称。</summary>
public sealed record TranslationLanguageOption(string Code, string? NameKey = null) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string DisplayName => NameKey is { } key ? Localization.Get(key) : NativeName ?? Code;
    public string DisplayCode => $"({Code})";

    internal string? NativeName
    {
        get
        {
            try
            {
                return CultureInfo.GetCultureInfo(Code).NativeName;
            }
            catch (CultureNotFoundException)
            {
                return null;
            }
        }
    }

    internal static IReadOnlyList<TranslationLanguageOption> Presets { get; } =
    [
        new("zh-CN", "Settings.TargetLanguage.ChineseSimplified"),
        new("zh-TW", "Settings.TargetLanguage.ChineseTraditional"),
        new("en-US", "Settings.TargetLanguage.EnglishUS"),
        new("en-GB", "Settings.TargetLanguage.EnglishUK"),
        new("ja-JP", "Settings.TargetLanguage.Japanese"),
        new("ko-KR", "Settings.TargetLanguage.Korean"),
        new("fr-FR", "Settings.TargetLanguage.French"),
        new("de-DE", "Settings.TargetLanguage.German"),
        new("es-ES", "Settings.TargetLanguage.Spanish"),
        new("pt-BR", "Settings.TargetLanguage.Portuguese"),
        new("ru-RU", "Settings.TargetLanguage.Russian"),
        new("it-IT", "Settings.TargetLanguage.Italian"),
        new("ar-SA", "Settings.TargetLanguage.Arabic"),
        new("th-TH", "Settings.TargetLanguage.Thai"),
        new("vi-VN", "Settings.TargetLanguage.Vietnamese"),
        new("id-ID", "Settings.TargetLanguage.Indonesian")
    ];

    internal void RefreshLocalization()
    {
        PropertyChanged?.Invoke(this, new(nameof(DisplayName)));
    }
}
