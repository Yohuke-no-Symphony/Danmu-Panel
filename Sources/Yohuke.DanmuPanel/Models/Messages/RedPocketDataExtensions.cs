using System.Globalization;
using Yohuke.DanmuCore.Apis.Models.Bilibili;

namespace Yohuke.DanmuPanel.Models.Messages;

internal static class RedPocketDataExtensions
{
    internal static string GetRecordId(this RedPocketData data, string roomTabId, string roomId)
    {
        return $"red-pocket:{roomTabId}:{roomId}:{data.LotId.ToString(CultureInfo.InvariantCulture)}";
    }

    internal static RedPocketData CreateSnapshot(this RedPocketData data)
    {
        return data with
        {
            Awards = Array.AsReadOnly(data.Awards.ToArray()),
            Winners = Array.AsReadOnly(data.Winners.ToArray())
        };
    }
}
