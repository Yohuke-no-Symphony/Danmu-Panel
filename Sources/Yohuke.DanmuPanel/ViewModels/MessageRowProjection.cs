using System.Globalization;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Services.Translation;

namespace Yohuke.DanmuPanel.ViewModels;

internal sealed class MessageRowProjection
{
    private const int MAX_MEDAL_NAME_TEXT_ELEMENTS = 3;

    internal MessageRecord Record { get; }
    internal MessageDisplaySettings Settings { get; }
    internal int FilterVersion { get; }
    internal bool TranslationBlocked { get; }
    internal bool TranslationPending { get; }
    internal bool HasTranslatableContent { get; }
    internal bool IsEventSummary { get; }
    internal string Name { get; }
    internal MessageReply? Reply { get; }
    internal bool IsSpecialAttention { get; }
    internal bool CanShowNamePinyin { get; }
    internal bool CanShowContentPinyin { get; }
    internal string Medal { get; }
    internal string MedalName { get; }
    internal string Time { get; }
    internal string? Detail { get; }
    internal string Price { get; }
    internal string SummaryLabel { get; } = "";
    internal string SummaryValue { get; } = "";
    internal string Translation { get; }

    internal static MessageRowProjection Create(MessageRecord record, MessageDisplaySettings display,
        bool isTranslationPending, bool isEventSummary = false)
    {
        return new(record, display, isTranslationPending, isEventSummary);
    }

    private MessageRowProjection(MessageRecord record, MessageDisplaySettings display, bool isTranslationPending,
        bool isEventSummary)
    {
        Record = record;
        Settings = display;
        FilterVersion = display.DanmuFilter.StateVersion;
        TranslationBlocked = display.IsTranslationBlocked(record);
        TranslationPending = isTranslationPending && !TranslationBlocked;
        IsEventSummary = isEventSummary && record.Kind != MessageKind.Danmu;
        var hasLocalizedText = display.LanguageId.Length > 0;
        Name = record.Kind == MessageKind.RedPocket && string.IsNullOrWhiteSpace(record.UserName) && hasLocalizedText
            ? display.GetText("RedPocket.UnknownSender") : record.UserName;
        Reply = record.Kind == MessageKind.Danmu && !string.IsNullOrWhiteSpace(record.ReplyUserName)
            ? new(record.ReplyIsMystery ? 0 : record.ReplyUid, record.ReplyUserName, record.ReplyUserNameColor)
            : null;
        IsSpecialAttention = record.Kind == MessageKind.Danmu && display.IsSpecialAttention(record.UserUid);
        CanShowNamePinyin = display.ShowUserPinyin && ContainsChinese(record.UserName) && !record.UserName.Any(character =>
            character is >= '\u3040' and <= '\u30FF' or >= '\uFF66' and <= '\uFF9F');
        CanShowContentPinyin = display.IsContentPinyinEnabled(record.Kind) && (record.Kind switch
        {
            MessageKind.Danmu => PinyinService.CanAnnotateDanmu(record),
            MessageKind.Gift => ContainsChinese(record.GiftName),
            _ => ContainsChinese(record.Content)
        });
        Medal = record.MedalLevel > 0 ? string.Create(display.Culture, $"{record.MedalName} {record.MedalLevel}") : "";
        MedalName = TrimMedalName(record.MedalName);
        Time = record.Time.ToLocalTime().ToString("HH:mm:ss", display.Culture);
        Detail = record.Kind switch
        {
            MessageKind.Gift => string.Create(display.Culture, $"{record.GiftName} ×{record.Quantity} · ￥{record.TotalPrice:0.##}"),
            MessageKind.Guard => hasLocalizedText
                ? display.GetText($"Panel.Guard.{record.GuardLevel}") + string.Create(display.Culture, $" ×{record.Quantity}")
                : null,
            _ => record.Content
        };
        Price = record.Kind == MessageKind.SuperChat ? string.Create(display.Culture, $"￥{record.TotalPrice:0.##}") : "";
        Translation = !TranslationBlocked && record.TranslationFingerprint == display.TranslationFingerprint
            ? record.Translation ?? "" : "";
        HasTranslatableContent = Translation.Length == 0 && display.TranslationEnabled && !TranslationBlocked &&
            record.Kind is MessageKind.Danmu or MessageKind.SuperChat &&
            TranslationTextExtractor.Extract(record.Content, record.Emoticons.Select(emoticon => emoticon.Text)).Length > 0;
        if (IsEventSummary && hasLocalizedText)
        {
            SummaryLabel = FormatSummaryLabel(record, display);
            SummaryValue = FormatSummaryValue(record, display);
        }
    }

    private static bool ContainsChinese(string text)
    {
        return text.Any(character => character is >= '\u4E00' and <= '\u9FFF');
    }

    private static string TrimMedalName(string text)
    {
        if (text.Length <= MAX_MEDAL_NAME_TEXT_ELEMENTS)
        {
            return text;
        }

        var name = new StringInfo(text);
        return name.LengthInTextElements <= MAX_MEDAL_NAME_TEXT_ELEMENTS
            ? text : name.SubstringByTextElements(0, MAX_MEDAL_NAME_TEXT_ELEMENTS);
    }

    private static string FormatSummaryLabel(MessageRecord record, MessageDisplaySettings display)
    {
        return record.Kind switch
        {
            MessageKind.SuperChat => display.GetText("Workbench.Panel.SuperChat"),
            MessageKind.Gift => display.FormatText("Panel.EventSummary.Gift", record.GiftName, record.Quantity),
            MessageKind.Guard => display.FormatText("Panel.EventSummary.Guard",
                display.GetText($"Panel.Guard.{record.GuardLevel}"), record.Quantity),
            MessageKind.RedPocket => display.FormatText("Panel.EventSummary.RedPocket",
                display.GetText(record.RedPocket?.HasResult == true ? "RedPocket.Drawn" : "RedPocket.Waiting")),
            _ => ""
        };
    }

    private static string FormatSummaryValue(MessageRecord record, MessageDisplaySettings display)
    {
        return record.Kind switch
        {
            MessageKind.RedPocket => record.RedPocket is { } redPocket
                ? RedPocketViewModel.FormatValue(redPocket, includeGiftLabel: false, display: display) : "",
            MessageKind.Guard when !record.HasKnownPrice && record.TotalPrice == 0 =>
                display.GetText("Panel.EventSummary.UnknownValue"),
            _ => display.FormatText("Panel.EventSummary.Value", record.TotalPrice)
        };
    }
}
