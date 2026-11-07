namespace Yohuke.DanmuPanel.Models.Messages;

/// <summary>可恢复的直播消息及其阅读与翻译状态。</summary>
public sealed record MessageRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string RoomTabId { get; init; } = "";
    public string RoomId { get; init; } = "";
    public MessageKind Kind { get; init; }
    public long UserUid { get; init; }
    public string UserName { get; init; } = "";

    /// <summary>消息发送者在本直播间的大航海档位，独立于所佩戴粉丝牌的身份。</summary>
    public int RoomGuardLevel { get; init; }

    /// <summary>消息发送者是否为本直播间的房管。</summary>
    public bool IsRoomAdmin { get; init; }

    public string Content { get; init; } = "";
    public string MessageId { get; init; } = "";
    public DateTimeOffset Time { get; init; } = DateTimeOffset.UtcNow;
    public bool HasSourceTimestamp { get; init; } = true;
    public bool IsHistory { get; init; }

    /// <summary>标识由测试面板生成的消息，供独立清理使用。</summary>
    public bool IsTest { get; init; }

    public long Generation { get; init; }
    public IReadOnlyList<MessageEmoticon> Emoticons { get; init; } = [];
    public string EmoticonUrl { get; init; } = "";
    public string MedalName { get; init; } = "";
    public int MedalLevel { get; init; }
    public int MedalGuardLevel { get; init; }
    public string MedalColor { get; init; } = "";
    public string MedalColorStart { get; init; } = "";
    public string MedalColorEnd { get; init; } = "";
    public string MedalColorBorder { get; init; } = "";
    public bool IsMedalLighted { get; init; }
    public string GiftName { get; init; } = "";
    public string GiftId { get; init; } = "";
    public string GiftCoinType { get; init; } = "gold";
    public string GiftTransactionId { get; init; } = "";
    public int Quantity { get; init; } = 1;
    public int BatchComboCount { get; init; }
    public decimal UnitPrice { get; init; }
    public decimal TotalPrice { get; init; }
    public string ComboId { get; init; } = "";
    public int GuardLevel { get; init; }
    public string BorderColor { get; init; } = "";
    public bool IsRead { get; init; }
    public bool IsPinned { get; init; }
    public bool IsPriority { get; init; }
    public DateTimeOffset? PinnedAt { get; init; }
    public bool HasBeenRead { get; init; }
    public int ReadQuantity { get; init; }
    public decimal ReadTotalPrice { get; init; }
    public string? Translation { get; init; }
    public string? TranslationFingerprint { get; init; }
}
