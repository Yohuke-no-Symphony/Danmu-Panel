using Yohuke.DanmuPanel.Apis.Models.Bilibili;
using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.Services;

/// <summary>将 B 站协议转换为不依赖界面的消息记录。</summary>
public static class RoomMessageMapper
{
    /// <summary>转换普通弹幕，保留表情与粉丝牌。</summary>
    public static MessageRecord Map(BilibiliDanmuMessage value, string tabId, long generation, bool history = false)
    {
        return new()
        {
            RoomTabId = tabId, Generation = generation, RoomId = value.RoomId,
            Kind = MessageKind.Danmu, MessageId = value.MessageId, Time = Time(value.Timestamp),
            HasSourceTimestamp = value.Timestamp > 0,
            UserUid = value.Uid, UserName = value.UserName, Content = value.Content,
            RoomGuardLevel = value.RoomGuardLevel, IsRoomAdmin = value.IsRoomAdmin,
            IsHistory = history, MedalName = value.MedalName, MedalLevel = value.MedalLevel,
            MedalGuardLevel = value.MedalGuardLevel,
            Emoticons = value.Emoticons.Select(item => new MessageEmoticon { Text = item.Text, Url = item.Url }).ToArray()
        };
    }

    /// <summary>转换礼物，价格统一转换为人民币元。</summary>
    public static MessageRecord Map(BilibiliGiftMessage value, string tabId, long generation)
    {
        var unitPrice = string.Equals(value.CoinType, "gold", StringComparison.OrdinalIgnoreCase)
            ? value.UnitPrice / 1000m
            : 0;
        return new()
        {
            RoomTabId = tabId, Generation = generation, RoomId = value.RoomId,
            Kind = MessageKind.Gift, MessageId = value.MessageId, Time = Time(value.Timestamp),
            HasSourceTimestamp = value.Timestamp > 0,
            UserUid = value.Uid, UserName = value.UserName, GiftName = value.GiftName,
            GiftId = value.GiftId, Quantity = value.Quantity, BatchComboCount = value.BatchComboCount,
            GiftCoinType = value.CoinType, GiftTransactionId = value.TransactionId,
            UnitPrice = unitPrice, TotalPrice = unitPrice * value.Quantity,
            ComboId = value.BatchComboId
        };
    }

    /// <summary>转换醒目留言。</summary>
    public static MessageRecord Map(BilibiliSuperChatMessage value, string tabId, long generation)
    {
        return new()
        {
            RoomTabId = tabId, Generation = generation, RoomId = value.RoomId,
            Kind = MessageKind.SuperChat, MessageId = value.MessageId, Time = Time(value.Timestamp),
            HasSourceTimestamp = value.Timestamp > 0,
            UserUid = value.Uid, UserName = value.UserName, Content = value.Content,
            TotalPrice = value.Price, BorderColor = value.BorderColor
        };
    }

    /// <summary>转换独立的上舰事件。</summary>
    public static MessageRecord Map(BilibiliGuardPurchaseMessage value, string tabId, long generation)
    {
        var quantity = Math.Max(1, value.Quantity);
        var totalPrice = value.TotalPrice is >= 0 ? value.TotalPrice : null;
        return new()
        {
            RoomTabId = tabId, Generation = generation, RoomId = value.RoomId,
            Kind = MessageKind.Guard, MessageId = value.Identity, Time = Time(value.Timestamp),
            HasSourceTimestamp = value.Timestamp > 0,
            UserUid = value.Uid, UserName = value.UserName, GuardLevel = value.GuardLevel,
            Quantity = quantity,
            UnitPrice = totalPrice.GetValueOrDefault() / quantity,
            TotalPrice = totalPrice.GetValueOrDefault(),
            HasKnownPrice = totalPrice.HasValue
        };
    }

    private static DateTimeOffset Time(long timestamp)
    {
        return timestamp > 0 ? DateTimeOffset.FromUnixTimeSeconds(timestamp) : DateTimeOffset.UtcNow;
    }
}
