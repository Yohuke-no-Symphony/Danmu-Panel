using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Models.Testing;
using Yohuke.DanmuPanel.Services.Appearance;
using Yohuke.DanmuPanel.Services.Messages;
using Yohuke.DanmuPanel.Services.Translation;

namespace Yohuke.DanmuPanel.Services;

/// <summary>生成本地测试消息，并按全局设置接入现有自动翻译调度。</summary>
public sealed class TestMessageService
{
    private readonly ITestMessageStore store;
    private readonly Func<PanelSettings> getSettings;
    private readonly Action<string> notifyChanged;
    private readonly Action notifyTranslation;

    /// <summary>连接测试存储、当前设置、消息刷新和翻译调度通知。</summary>
    public TestMessageService(ITestMessageStore store, Func<PanelSettings> getSettings, Action<string> notifyChanged,
        Action notifyTranslation)
    {
        this.store = store;
        this.getSettings = getSettings;
        this.notifyChanged = notifyChanged;
        this.notifyTranslation = notifyTranslation;
    }

    /// <summary>原子添加消息及可选翻译任务；人工译文按当前配置标记为已完成。</summary>
    public async Task<int> AddAsync(RoomTabState room, TestMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.UserName);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.Count, TestMessageRequest.MAX_BATCH_COUNT);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Quantity, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(request.UnitPrice);
        ArgumentOutOfRangeException.ThrowIfNegative(request.MedalLevel);
        ArgumentOutOfRangeException.ThrowIfNegative(request.MedalGuardLevel);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.MedalGuardLevel, 3);
        ArgumentOutOfRangeException.ThrowIfNegative(request.RoomGuardLevel);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.RoomGuardLevel, 3);
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
        var medalName = request.MedalName.Trim();
        var hasMedal = request.Kind == MessageKind.Danmu && medalName.Length > 0 && request.MedalLevel > 0;
        var quantity = request.Kind is MessageKind.Gift or MessageKind.Guard ? request.Quantity : 1;
        var hasText = request.Kind is MessageKind.Danmu or MessageKind.SuperChat;
        var translation = hasText && !string.IsNullOrWhiteSpace(request.Translation) ? request.Translation : null;
        var fingerprint = hasText && (settings.Translation.Enabled || translation is not null)
            ? TranslationRequestFactory.GetConfigurationFingerprint(settings.Translation) : null;
        var requestFingerprint = hasText && settings.Translation.Enabled ? fingerprint : null;
        var now = DateTimeOffset.UtcNow;
        var messages = Enumerable.Range(0, request.Count).Select(index => new MessageRecord
        {
            RoomTabId = room.Id,
            RoomId = room.RoomId,
            Kind = request.Kind,
            IsTest = true,
            UserName = request.Count > 1 ? $"{request.UserName.Trim()} · {index + 1}" : request.UserName.Trim(),
            MedalName = hasMedal ? medalName : "",
            MedalLevel = hasMedal ? request.MedalLevel : 0,
            MedalGuardLevel = request.Kind == MessageKind.Danmu ? request.MedalGuardLevel : 0,
            RoomGuardLevel = request.Kind == MessageKind.Danmu ? request.RoomGuardLevel : 0,
            IsRoomAdmin = request.Kind == MessageKind.Danmu && request.IsRoomAdmin,
            Content = hasText ? request.Content : "",
            Time = now.AddMilliseconds(index - request.Count + 1),
            GiftName = request.Kind == MessageKind.Gift ? request.GiftName.Trim() : "",
            Quantity = quantity,
            UnitPrice = request.Kind == MessageKind.Danmu ? 0 : request.UnitPrice,
            TotalPrice = request.Kind == MessageKind.Danmu ? 0 : request.UnitPrice * quantity,
            GuardLevel = request.Kind == MessageKind.Guard ? request.GuardLevel : 0,
            BorderColor = request.Kind == MessageKind.SuperChat
                ? ColorSettingCatalog.GetSuperChatBorder(request.UnitPrice).DefaultColor!
                : request.Kind == MessageKind.Guard
                    ? request.GuardLevel switch { 1 => "#D99D36", 2 => "#9554B2", _ => "#2A60B2" } : "",
            Translation = translation,
            TranslationFingerprint = fingerprint
        }).ToArray();
        var added = await store.AppendTestMessagesAsync(messages, settings.Reading, requestFingerprint, cancellationToken);
        if (requestFingerprint is not null && translation is null)
        {
            notifyTranslation();
        }
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
