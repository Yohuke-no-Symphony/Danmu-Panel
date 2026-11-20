using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.ViewModels;

internal readonly record struct FeedProjectionContext(MessageDisplaySettings Display, int FilterVersion,
    MessageSortOrder SortOrder, int RetentionCount, bool HideEmoticonDanmu, int OptionsRevision);
