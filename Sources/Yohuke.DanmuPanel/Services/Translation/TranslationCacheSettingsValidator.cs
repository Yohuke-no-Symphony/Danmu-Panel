using Yohuke.DanmuPanel.Models.Translation;

namespace Yohuke.DanmuPanel.Services.Translation;

internal static class TranslationCacheSettingsValidator
{
    internal static void Validate(TranslationSettings? settings)
    {
        if (settings is null || settings.CacheCapacity is < 0 or > TranslationSettings.MAX_CACHE_CAPACITY ||
            settings.CacheTtlMinutes is < TranslationSettings.MIN_CACHE_TTL_MINUTES or > TranslationSettings.MAX_CACHE_TTL_MINUTES)
        {
            throw new InvalidDataException("The translation cache settings are outside the supported range.");
        }
    }
}
