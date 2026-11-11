using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.Services.Messages;

internal static class MessageSortSettingsValidator
{
    internal static void Validate(MessageSortSettings? settings)
    {
        if (settings is null)
        {
            throw new InvalidDataException("The message sorting settings are missing.");
        }
        ValidatePanel(settings.SuperChat);
        ValidatePanel(settings.Gift);
        ValidatePanel(settings.Guard);
        ValidatePanel(settings.RedPocket);
    }

    private static void ValidatePanel(MessageReadSortSettings? panel)
    {
        if (panel is null || !Enum.IsDefined(panel.Unread) || !Enum.IsDefined(panel.Read))
        {
            throw new InvalidDataException("The message sort order is invalid.");
        }
    }
}
