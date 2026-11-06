using Yohuke.DanmuPanel.Models.State;

namespace Yohuke.DanmuPanel.Services.Messages;

internal static class MessageRetentionSettingsValidator
{
    internal static void Validate(int count)
    {
        if (count is < 1 or > PanelSettings.MAX_MESSAGE_RETENTION_COUNT)
        {
            throw new InvalidDataException("The message retention count is outside the supported range.");
        }
    }
}
