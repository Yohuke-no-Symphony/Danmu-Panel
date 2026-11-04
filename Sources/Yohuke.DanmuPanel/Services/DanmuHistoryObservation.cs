using Yohuke.DanmuPanel.Apis.Models.Bilibili;

namespace Yohuke.DanmuPanel.Services;

internal sealed class DanmuHistoryObservation(BilibiliDanmuMessage message, bool isHistory)
{
    internal BilibiliDanmuMessage Message { get; } = message;
    internal bool IsHistory { get; } = isHistory;
    internal bool MatchedLive { get; set; }
}
