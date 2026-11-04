using System.Collections.Frozen;

namespace Yohuke.DanmuPanel.I18n;

internal sealed record LanguagePack(LanguageInfo Info, FrozenDictionary<string, string> Strings);
