using System.Collections.Specialized;

namespace Yohuke.DanmuPanel.ViewModels;

internal readonly record struct FeedCollectionEdit<T>(NotifyCollectionChangedAction Action, int Index, int Count = 1,
    IReadOnlyList<T>? Values = null, int OldIndex = -1) where T : class;
