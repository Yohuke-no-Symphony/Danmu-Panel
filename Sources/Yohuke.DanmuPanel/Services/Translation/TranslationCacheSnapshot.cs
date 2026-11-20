namespace Yohuke.DanmuPanel.Services.Translation;

internal sealed record TranslationCacheSnapshot(int Version, TranslationCacheEntry[] Entries);
