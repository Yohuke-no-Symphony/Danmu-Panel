using System.Globalization;

namespace Yohuke.DanmuPanel.I18n;

internal sealed record LocalizationSnapshot(
    LocalizationCatalog Catalog,
    CultureInfo SystemCulture,
    string SelectedLanguageID,
    string CurrentLanguageID);
