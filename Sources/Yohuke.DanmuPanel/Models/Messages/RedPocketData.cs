using System.Globalization;

namespace Yohuke.DanmuPanel.Models.Messages;

/// <summary>聚合同一红包的发包资料、奖品与开奖结果。</summary>
public sealed record RedPocketData
{
    public long LotId { get; init; }
    public int? Type { get; init; }
    public long SenderUid { get; init; }
    public string SenderName { get; init; } = "";
    public string SenderAvatarUrl { get; init; } = "";
    public bool? IsMystery { get; init; }
    public long StartTime { get; init; }
    public long EndTime { get; init; }
    public long CurrentTime { get; init; }
    public int? Duration { get; init; }
    public int? LotStatus { get; init; }
    public int? UserStatus { get; init; }
    public int? JoinRequirement { get; init; }
    public string Danmu { get; init; } = "";
    public string H5Url { get; init; } = "";
    public decimal? Price { get; init; }
    public decimal? TotalPrice { get; init; }
    public decimal? TotalBattery { get; init; }
    public int? TotalAwardCount { get; init; }
    public bool HasDetails { get; init; }
    public bool HasResult { get; init; }
    public IReadOnlyList<RedPocketAward> Awards { get; init; } = [];
    public IReadOnlyList<RedPocketWinner> Winners { get; init; } = [];

    internal string GetRecordId(string roomTabId, string roomId)
    {
        return $"red-pocket:{roomTabId}:{roomId}:{LotId.ToString(CultureInfo.InvariantCulture)}";
    }

    internal RedPocketData CreateSnapshot()
    {
        return this with
        {
            Awards = Array.AsReadOnly(Awards.ToArray()),
            Winners = Array.AsReadOnly(Winners.ToArray())
        };
    }
}
