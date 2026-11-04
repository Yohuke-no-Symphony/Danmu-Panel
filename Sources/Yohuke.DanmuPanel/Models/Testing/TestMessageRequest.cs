using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.Models.Testing;

/// <summary>一次本地测试消息生成请求，金额使用人民币元。</summary>
public sealed record TestMessageRequest
{
    public const int MAX_BATCH_COUNT = 500;
    public MessageKind Kind { get; init; }
    public string UserName { get; init; } = "";
    public string Content { get; init; } = "";
    public string GiftName { get; init; } = "";
    public decimal UnitPrice { get; init; }
    public int Quantity { get; init; } = 1;
    public int GuardLevel { get; init; } = 3;
    public string Translation { get; init; } = "";
    public int Count { get; init; } = 1;
}
