namespace Yohuke.DanmuPanel.Services.Translation;

internal readonly record struct TranslationCacheEntry(string Key, string Translation, DateTimeOffset StoredAtUtc);
