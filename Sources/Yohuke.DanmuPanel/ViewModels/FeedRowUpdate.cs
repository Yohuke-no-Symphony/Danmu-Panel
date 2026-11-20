namespace Yohuke.DanmuPanel.ViewModels;

internal readonly record struct FeedRowUpdate(MessageItemViewModel? ExistingItem, MessageRowProjection Projection);
