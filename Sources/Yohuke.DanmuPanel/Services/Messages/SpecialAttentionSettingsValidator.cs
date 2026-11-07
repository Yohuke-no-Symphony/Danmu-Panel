using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.Services.Messages;

internal static class SpecialAttentionSettingsValidator
{
    internal static void Validate(SpecialAttentionSettings? settings)
    {
        if (settings is null || settings.Users is null)
        {
            throw new InvalidDataException("The special attention settings are missing.");
        }

        var userUids = new HashSet<long>();
        foreach (var user in settings.Users)
        {
            if (user is null || user.UserUid <= 0 || user.DisplayName is null || !userUids.Add(user.UserUid))
            {
                throw new InvalidDataException("Special attention requires distinct positive user UIDs and valid display names.");
            }
        }
    }
}
