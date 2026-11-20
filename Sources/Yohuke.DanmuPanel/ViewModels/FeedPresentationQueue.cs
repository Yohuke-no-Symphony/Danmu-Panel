namespace Yohuke.DanmuPanel.ViewModels;

internal sealed class FeedPresentationQueue
{
    private readonly LinkedList<MessageItemViewModel> visibleRows = [];
    private readonly LinkedList<MessageItemViewModel> otherRows = [];
    private readonly Dictionary<MessageItemViewModel, LinkedListNode<MessageItemViewModel>> nodes = [];

    internal bool HasWaiting => visibleRows.Count > 0 || otherRows.Count > 0;
    internal bool IsRunning { get; set; }

    internal void Enqueue(MessageItemViewModel item, bool visible = false)
    {
        if (!item.IsPresentationPending)
        {
            return;
        }
        if (nodes.TryGetValue(item, out var node))
        {
            if (visible && node.List == otherRows)
            {
                otherRows.Remove(node);
                visibleRows.AddLast(node);
            }
            return;
        }
        nodes.Add(item, (visible ? visibleRows : otherRows).AddLast(item));
    }

    internal MessageItemViewModel? Take()
    {
        var rows = visibleRows.Count > 0 ? visibleRows : otherRows;
        if (rows.First is not { } node)
        {
            return null;
        }
        rows.RemoveFirst();
        return node.Value;
    }

    internal void Deprioritize(MessageItemViewModel item)
    {
        if (nodes.TryGetValue(item, out var node) && node.List == visibleRows)
        {
            visibleRows.Remove(node);
            otherRows.AddLast(node);
        }
    }

    internal void Remove(MessageItemViewModel item)
    {
        if (nodes.Remove(item, out var node))
        {
            node.List?.Remove(node);
        }
    }

    internal void RemoveInactive(Func<MessageItemViewModel, bool> isActive)
    {
        RemoveFrom(visibleRows);
        RemoveFrom(otherRows);

        void RemoveFrom(LinkedList<MessageItemViewModel> rows)
        {
            var node = rows.First;
            while (node is not null)
            {
                var next = node.Next;
                if (!isActive(node.Value))
                {
                    Remove(node.Value);
                }
                node = next;
            }
        }
    }

    internal void Clear()
    {
        visibleRows.Clear();
        otherRows.Clear();
        nodes.Clear();
    }
}
