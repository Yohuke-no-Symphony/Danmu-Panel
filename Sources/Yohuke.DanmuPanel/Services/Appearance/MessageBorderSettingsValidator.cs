using Yohuke.DanmuPanel.Models.Appearance;

namespace Yohuke.DanmuPanel.Services.Appearance;

internal static class MessageBorderSettingsValidator
{
    internal static void Validate(MessageBorderSettings? settings)
    {
        if (settings is null)
        {
            throw new InvalidDataException("The message border settings are missing.");
        }
        if (!IsValidThickness(settings.SuperChat) || !IsValidThickness(settings.Gift) || !IsValidThickness(settings.Guard))
        {
            throw new InvalidDataException("Message border thickness must be finite and between 0.5 and 10.");
        }
    }

    private static bool IsValidThickness(double thickness)
    {
        return double.IsFinite(thickness) &&
            thickness is >= MessageBorderSettings.MIN_THICKNESS and <= MessageBorderSettings.MAX_THICKNESS;
    }
}
