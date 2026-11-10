using System.Collections.Specialized;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed class MessageFeedListBox(Func<MessageFeedRow> createRow, MessageKind kind = MessageKind.Danmu) : ListBox
{
    private readonly HashSet<MessageFeedListBoxItem> slots = [];
    private INotifyCollectionChanged? observedSource;
    private IDisposable? sourceSubscription;

    /// <inheritdoc/>
    protected override Type StyleKeyOverride => typeof(ListBox);

    /// <inheritdoc />
    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey)
    {
        recycleKey = item is MessageLoadingPlaceholderViewModel
            ? typeof(MessageLoadingListBoxItem)
            : typeof(MessageFeedListBoxItem);
        return true;
    }

    /// <inheritdoc/>
    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey)
    {
        if (item is MessageLoadingPlaceholderViewModel)
        {
            return new MessageLoadingListBoxItem(kind);
        }
        var row = createRow();
        row.DataContext = null;
        var container = new MessageFeedListBoxItem(row);
        RegisterSlot(container);
        return container;
    }

    /// <inheritdoc/>
    protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
    {
        base.PrepareContainerForItemOverride(container, item, index);
        if (container is MessageFeedListBoxItem messageContainer)
        {
            RegisterSlot(messageContainer);
            messageContainer.Row.UpdateAlternatingBackground(index);
            messageContainer.Content = messageContainer.Host;
        }
        else if (container is MessageLoadingListBoxItem placeholderContainer)
        {
            placeholderContainer.Content = placeholderContainer.Skeleton;
        }
    }

    /// <inheritdoc/>
    protected override void ClearContainerForItemOverride(Control container)
    {
        if (container is MessageFeedListBoxItem messageContainer)
        {
            messageContainer.Row.ContextMenu?.Close();
        }
        base.ClearContainerForItemOverride(container);
        container.DataContext = null;
    }

    /// <inheritdoc />
    protected override void ContainerIndexChangedOverride(Control container, int oldIndex, int newIndex)
    {
        base.ContainerIndexChangedOverride(container, oldIndex, newIndex);
        if (container is MessageFeedListBoxItem messageContainer)
        {
            messageContainer.Row.UpdateAlternatingBackground(newIndex);
        }
    }

    /// <inheritdoc />
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        ClearSlots();
        base.OnApplyTemplate(e);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == ItemsSourceProperty)
        {
            ObserveSource(null);
            ClearSlots();
        }
        else if (change.Property == ItemsPanelProperty || change.Property == TemplateProperty)
        {
            ClearSlots();
        }
        base.OnPropertyChanged(change);
        if (change.Property == ItemsSourceProperty)
        {
            ObserveSource(ItemsSource as INotifyCollectionChanged);
        }
    }

    private void ObserveSource(INotifyCollectionChanged? source)
    {
        if (ReferenceEquals(observedSource, source))
        {
            return;
        }
        sourceSubscription?.Dispose();
        observedSource = source;
        sourceSubscription = source?.WeakSubscribe(OnSourceChanged);
    }

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset || ItemCount == 0)
        {
            ClearSlots();
        }
    }

    private void RegisterSlot(MessageFeedListBoxItem container)
    {
        if (!slots.Add(container))
        {
            return;
        }
        container.ConnectRowDataContext();
        if (!LogicalChildren.Contains(container.Row))
        {
            LogicalChildren.Add(container.Row);
        }
    }

    private void ClearSlots()
    {
        var released = slots.ToArray();
        slots.Clear();
        foreach (var container in released)
        {
            container.Row.ContextMenu?.Close();
            container.DisconnectRowDataContext();
            LogicalChildren.Remove(container.Row);
        }
    }
}
