using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Models.Testing;
using Yohuke.DanmuPanel.Services.Messages;
using Yohuke.DanmuPanel.Services.Translation;

namespace Yohuke.DanmuPanel.Services;

/// <summary>生成仅在本地显示的测试数据，不使用直播连接或翻译客户端。</summary>
public sealed class TestMessageService
{
    private readonly ITestMessageStore store;
    private readonly Func<PanelSettings> getSettings;
    private readonly Action<string> notifyChanged;

    /// <summary>连接测试存储、当前设置和消息刷新通知。</summary>
    public TestMessageService(ITestMessageStore store, Func<PanelSettings> getSettings, Action<string> notifyChanged)
    {
        this.store = store;
        this.getSettings = getSettings;
        this.notifyChanged = notifyChanged;
    }

    /// <summary>原子添加一批本地消息；人工译文按当前翻译配置标记。</summary>
    public async Task<int> AddAsync(RoomTabState room, TestMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.UserName);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.Count, TestMessageRequest.MAX_BATCH_COUNT);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Quantity, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(request.UnitPrice);
        if (!Enum.IsDefined(request.Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }
        if (request.Kind is MessageKind.Danmu or MessageKind.SuperChat)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request.Content);
        }
        if (request.Kind == MessageKind.Gift)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request.GiftName);
        }
        if (request.Kind == MessageKind.Guard && request.GuardLevel is < 1 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        var settings = getSettings();
        var quantity = request.Kind is MessageKind.Gift or MessageKind.Guard ? request.Quantity : 1;
        var hasText = request.Kind is MessageKind.Danmu or MessageKind.SuperChat;
        var translation = hasText && !string.IsNullOrWhiteSpace(request.Translation) ? request.Translation : null;
        var fingerprint = translation is null ? null : TranslationRequestFactory.GetConfigurationFingerprint(settings.Translation);
        var now = DateTimeOffset.UtcNow;
        var messages = Enumerable.Range(0, request.Count).Select(index => new MessageRecord
        {
            RoomTabId = room.Id,
            RoomId = room.RoomId,
            Kind = request.Kind,
            IsTest = true,
            UserName = request.Count > 1 ? $"{request.UserName.Trim()} · {index + 1}" : request.UserName.Trim(),
            Content = hasText ? request.Content : "",
            Time = now.AddMilliseconds(index - request.Count + 1),
            GiftName = request.Kind == MessageKind.Gift ? request.GiftName.Trim() : "",
            Quantity = quantity,
            UnitPrice = request.Kind == MessageKind.Danmu ? 0 : request.UnitPrice,
            TotalPrice = request.Kind == MessageKind.Danmu ? 0 : request.UnitPrice * quantity,
            GuardLevel = request.Kind == MessageKind.Guard ? request.GuardLevel : 0,
            BorderColor = request.Kind == MessageKind.SuperChat ? "#1D71B8" : request.Kind == MessageKind.Guard
                ? request.GuardLevel switch { 1 => "#D99D36", 2 => "#9554B2", _ => "#2A60B2" } : "",
            Translation = translation,
            TranslationFingerprint = fingerprint
        }).ToArray();
        var added = await store.AppendTestMessagesAsync(messages, settings.Reading, cancellationToken);
        notifyChanged(room.Id);
        return added;
    }

    /// <summary>清除当前房间的测试消息并刷新阅读列表，保留真实消息和其他房间。</summary>
    public async Task<int> ClearAsync(RoomTabState room, CancellationToken cancellationToken = default)
    {
        var removed = await store.ClearTestMessagesAsync(room.Id, cancellationToken);
        notifyChanged(room.Id);
        return removed;
    }
}
