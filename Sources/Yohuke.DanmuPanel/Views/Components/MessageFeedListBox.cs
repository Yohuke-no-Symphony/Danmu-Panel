using System.Collections.Specialized;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.ViewModels;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed class MessageFeedListBox(Func<MessageFeedRow> createRow, MessageKind kind = MessageKind.Danmu) : ListBox
{
    private readonly HashSet<MessageFeedListBoxItem> slots = [];
    private HashSet<MessageItemViewModel> visibleMessages = [];
    private HashSet<MessageItemViewModel> previousVisibleMessages = [];
    private INotifyCollectionChanged? observedSource;
    private IDisposable? sourceSubscription;
    private ScrollContentPresenter? visibilityPresenter;
    private bool isVisibilityUpdateQueued;
    private long visibilityGeneration;

    internal event Action<MessageItemViewModel>? MessageBecameVisible;
    internal event Action<MessageItemViewModel>? MessageBecameHidden;

    internal bool IsMessageVisible(MessageItemViewModel message)
    {
        return IsEffectivelyVisible && visibleMessages.Contains(message);
    }

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
            QueueVisibilityUpdate();
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
            QueueVisibilityUpdate();
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
        visibilityPresenter = null;
        base.OnApplyTemplate(e);
        QueueVisibilityUpdate();
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (kind == MessageKind.Danmu)
        {
            AddHandler(ScrollViewer.ScrollChangedEvent, OnVisibilityScrollChanged, RoutingStrategies.Bubble, true);
            EffectiveViewportChanged += OnVisibilityViewportChanged;
            QueueVisibilityUpdate();
        }
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        EffectiveViewportChanged -= OnVisibilityViewportChanged;
        RemoveHandler(ScrollViewer.ScrollChangedEvent, OnVisibilityScrollChanged);
        visibilityPresenter = null;
        visibilityGeneration++;
        isVisibilityUpdateQueued = false;
        if (visibleMessages.Count > 0)
        {
            var departingMessages = visibleMessages.ToArray();
            visibleMessages.Clear();
            foreach (var message in departingMessages)
            {
                MessageBecameHidden?.Invoke(message);
            }
        }
        base.OnDetachedFromVisualTree(e);
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
        if (change.Property == ItemsSourceProperty || change.Property == ItemsPanelProperty ||
            change.Property == TemplateProperty || change.Property == IsVisibleProperty || change.Property == BoundsProperty)
        {
            QueueVisibilityUpdate();
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
            QueueVisibilityUpdate();
        }
    }

    private void RegisterSlot(MessageFeedListBoxItem container)
    {
        if (!slots.Add(container))
        {
            return;
        }
        container.ConnectRowDataContext();
        if (kind == MessageKind.Danmu)
        {
            container.PropertyChanged += OnVisibilityContainerChanged;
        }
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
            container.PropertyChanged -= OnVisibilityContainerChanged;
            container.DisconnectRowDataContext();
            LogicalChildren.Remove(container.Row);
        }
    }

    private void OnVisibilityScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, Scroll))
        {
            QueueVisibilityUpdate();
        }
    }

    private void OnVisibilityViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        QueueVisibilityUpdate();
    }

    private void OnVisibilityContainerChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == BoundsProperty || e.Property == DataContextProperty || e.Property == IsVisibleProperty)
        {
            QueueVisibilityUpdate();
        }
    }

    private void QueueVisibilityUpdate()
    {
        if (isVisibilityUpdateQueued || MessageBecameVisible is null && MessageBecameHidden is null ||
            this.GetVisualRoot() is null)
        {
            return;
        }
        isVisibilityUpdateQueued = true;
        var generation = visibilityGeneration;
        Dispatcher.UIThread.Post(() => UpdateVisibleMessages(generation), DispatcherPriority.Loaded);
    }

    private void UpdateVisibleMessages(long generation)
    {
        if (generation != visibilityGeneration)
        {
            return;
        }
        isVisibilityUpdateQueued = false;

        if (IsEffectivelyVisible && GetVisibilityPresenter() is { } presenter &&
            presenter.Bounds.Width > 0 && presenter.Bounds.Height > 0)
        {
            var viewport = new Rect(presenter.Bounds.Size);
            foreach (var container in GetRealizedContainers())
            {
                if (container is MessageFeedListBoxItem { IsEffectivelyVisible: true, DataContext: MessageItemViewModel message } &&
                    container.Bounds.Width > 0 && container.Bounds.Height > 0 &&
                    container.TranslatePoint(default, presenter) is { } origin &&
                    viewport.Intersects(new Rect(origin, container.Bounds.Size)))
                {
                    previousVisibleMessages.Add(message);
                }
            }
        }

        (visibleMessages, previousVisibleMessages) = (previousVisibleMessages, visibleMessages);
        foreach (var message in previousVisibleMessages)
        {
            if (!visibleMessages.Contains(message))
            {
                MessageBecameHidden?.Invoke(message);
                if (generation != visibilityGeneration)
                {
                    previousVisibleMessages.Clear();
                    return;
                }
            }
        }
        foreach (var message in visibleMessages)
        {
            if (!previousVisibleMessages.Contains(message))
            {
                MessageBecameVisible?.Invoke(message);
                if (generation != visibilityGeneration)
                {
                    break;
                }
            }
        }
        previousVisibleMessages.Clear();
    }

    private ScrollContentPresenter? GetVisibilityPresenter()
    {
        if (Scroll is not ScrollViewer viewer)
        {
            return null;
        }
        if (visibilityPresenter is null || !ReferenceEquals(visibilityPresenter.GetVisualRoot(), this.GetVisualRoot()))
        {
            visibilityPresenter = viewer.GetVisualDescendants().OfType<ScrollContentPresenter>()
                .FirstOrDefault(candidate => ReferenceEquals(candidate.TemplatedParent, viewer));
        }
        return visibilityPresenter;
    }
}
