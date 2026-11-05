using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.Models.Translation;

internal sealed record MessagePresentationSnapshot(
    MessageRecord Source,
    MessagePresentationOptions Options,
    string NamePinyin,
    IReadOnlyList<PinyinAnnotation> NameAnnotations,
    string DetailPinyin,
    IReadOnlyList<PinyinAnnotation> ContentPinyin,
    bool HasTranslatableContent)
{
    internal bool Matches(MessageRecord record, MessagePresentationOptions options)
    {
        return Source.Kind == record.Kind && Source.UserName == record.UserName && Source.Content == record.Content &&
            Source.GiftName == record.GiftName && Source.Quantity == record.Quantity && Source.TotalPrice == record.TotalPrice &&
            Source.Emoticons.SequenceEqual(record.Emoticons) && Options == options;
    }
}
