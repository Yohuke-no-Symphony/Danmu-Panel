namespace Yohuke.DanmuPanel.Services.Messages;

internal sealed record GiftComboState(int Quantity, decimal TotalPrice, int ClearedQuantity,
    decimal ClearedTotalPrice, string? MessageId);
