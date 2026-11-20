using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.ViewModels;

internal readonly record struct FeedItemState(MessageItemViewModel Item, MessageRecord Record, MessageDisplaySettings Display,
    int FilterVersion, bool TranslationPending, bool TranslationBlocked);
