using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.Services.Messages;

internal static class MessageColumnSettingsValidator
{
    internal static void Validate(MessageColumnSettings? settings)
    {
        if (settings is null)
        {
            throw new InvalidDataException("The message column settings are missing.");
        }
        ValidateWidths(settings.Danmu);
        ValidateWidths(settings.Gift);
        ValidateWidths(settings.Guard);
    }

    private static void ValidateWidths(MessageColumnWidthSettings? widths)
    {
        if (widths is null || !IsValidRatio(widths.RegularIdentityRatio) || !IsValidRatio(widths.CompactIdentityRatio))
        {
            throw new InvalidDataException("Message column ratios must be finite and greater than zero and less than one.");
        }
    }

    private static bool IsValidRatio(double ratio)
    {
        return double.IsFinite(ratio) && ratio is > 0 and < 1;
    }
}
