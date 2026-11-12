namespace Yohuke.DanmuPanel.Tests.Integration;

internal static class ReceivedGiftTestData
{
    internal static IReadOnlyList<BilibiliGiftMessage> CreateBatch()
    {
        return new (string GiftId, string Name, int Quantity, decimal UnitPrice)[]
        {
            ("32128", "爱心抱枕", 4, 16000),
            ("32125", "电影票", 1, 2000),
            ("32126", "棉花糖", 5, 9000)
        }.Select(gift => new BilibiliGiftMessage
        {
            RoomId = "100",
            MessageId = $"received-{gift.GiftId}",
            TransactionId = "test-ten-draw-transaction",
            BatchComboId = "test-ten-draw-combo",
            Timestamp = 1700000000,
            Uid = 42,
            UserName = "测试用户",
            GiftId = gift.GiftId,
            GiftName = gift.Name,
            Quantity = gift.Quantity,
            CoinType = "gold",
            UnitPrice = gift.UnitPrice
        }).ToArray();
    }
}
