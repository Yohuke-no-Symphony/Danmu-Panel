using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Views;

/// <summary>使用虚拟化容器展示可点击的消息行。</summary>
public sealed class MessageFeedView : UserControl, IWorkbenchHeaderActions, IWorkbenchHeaderBadge, IWorkbenchHeaderTabs
{
    private readonly PanelRuntime runtime;
    private readonly Action<Exception> reportError;
    private readonly MessageKind messageKind;
    private readonly ListBox list;
    private readonly ListBox pinnedList;
    private readonly MessageColumnResizeController columnResize;
    private readonly MessageFeedTemplates templates = new();
    private DanmuFeedScrollController? scroll;
    private bool isScrollToTopPending;
    private bool columnWidthsDirty;
    private int columnWidthRevision;
    private int scheduledColumnWidthRevision = -1;
    internal Task PendingColumnWidthSave { get; private set; } = Task.CompletedTask;
    internal MessageCopyPopup? CopyPopup { get; private set; }
    internal EmoticonBitmapCache EmoticonBitmaps { get; } = new();
    public FeedViewModel Model { get; }
    public MessageFeedHeaderViewModel Header { get; }

    /// <summary>为每个停靠或浮动宿主创建独立的面板操作按钮。</summary>
    public IDataTemplate HeaderActionsTemplate { get; }

    /// <inheritdoc />
    public IDataTemplate? HeaderBadgeTemplate { get; }

    /// <inheritdoc />
    public IDataTemplate? HeaderTabsTemplate { get; }

    /// <summary>创建指定消息面板。</summary>
    public MessageFeedView(PanelRuntime runtime, MessageKind kind, Action<Exception> reportError)
    {
        this.runtime = runtime;
        this.reportError = reportError;
        messageKind = kind;
        Model = new(runtime, kind);
        Header = new(Model, () => ClearAsync(kind), ScrollToLatestAsync, ScrollToTopAsync, reportError);
        columnResize = new(this, (ratio, completed) =>
            OnColumnResizeRequested(this, new(ratio, completed)));
        AddHandler(MessageFeedColumnLayout.ResizeStartedEvent, OnColumnResizeStarted);
        AddHandler(MessageFeedColumnLayout.ResizeRequestedEvent, OnColumnResizeRequested);
        AddHandler(EmoticonTextBlock.AsyncContentUpdatingEvent, (_, _) => scroll?.BeginViewportUpdate());
        AddHandler(EmoticonTextBlock.AsyncContentUpdatedEvent, (_, _) =>
        {
            scroll?.NotifyLayoutChanged();
            scroll?.EndViewportUpdate();
        });
        UpdateTextScale();
        UpdateBorderThickness();
        Model.Failed += reportError;
        list = CreateList(Model.ScrollingRows, kind, "ScrollingMessages");
        pinnedList = CreateList(Model.PinnedItems, kind, "PinnedMessages");
        pinnedList.MaxHeight = 0;
        list.TemplateApplied += (_, _) => AttachScroll(list, kind);
        list.AttachedToVisualTree += (_, _) =>
        {
            Model.InitialLoadCompleted += OnInitialLoadCompleted;
            Model.ViewportUpdateStarted += OnViewportUpdateStarted;
            Model.ViewportUpdateCompleted += OnViewportUpdateCompleted;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => AttachScroll(list, kind));
        };
        list.DetachedFromVisualTree += (_, _) =>
        {
            Model.InitialLoadCompleted -= OnInitialLoadCompleted;
            Model.ViewportUpdateStarted -= OnViewportUpdateStarted;
            Model.ViewportUpdateCompleted -= OnViewportUpdateCompleted;
            scroll?.Dispose();
            scroll = null;
        };
        HeaderActionsTemplate = GetTemplate(kind == MessageKind.Danmu ? "DanmuHeaderActions" : "CardHeaderActions");
        if (kind is MessageKind.SuperChat or MessageKind.Gift or MessageKind.Guard)
        {
            HeaderBadgeTemplate = GetTemplate("UnreadBadge");
            HeaderTabsTemplate = GetTemplate("ReadTabs");
        }
        var surface = new MessageFeedSurface { DataContext = Model };
        surface.Host.Content = list;
        surface.PinnedContainer.Content = pinnedList;
        void UpdatePinnedHeight()
        {
            var available = Math.Max(0, surface.Bounds.Height - 24);
            pinnedList.MaxHeight = Model.HasScrollingItems ? available * 0.4 : available;
        }
        surface.SizeChanged += (_, _) => UpdatePinnedHeight();
        Model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(FeedViewModel.SelectedRoom) or nameof(FeedViewModel.ReadFilter) or nameof(FeedViewModel.SortOrder))
            {
                isScrollToTopPending = false;
                CancelColumnResizes();
                CopyPopup?.Hide();
            }
            else if (args.PropertyName == nameof(FeedViewModel.HasScrollingItems))
            {
                UpdatePinnedHeight();
                if (scroll is not null)
                {
                    UpdateReadingState(scroll.IsFollowingLatest);
                }
            }
            else if (args.PropertyName == nameof(FeedViewModel.MessageTextScale))
            {
                UpdateTextScale();
                scroll?.NotifyLayoutChanged();
            }
            else if (args.PropertyName == nameof(FeedViewModel.IsCompactDisplay))
            {
                CancelColumnResizes();
                UpdateColumnRatio();
                scroll?.NotifyLayoutChanged();
            }
            else if (args.PropertyName is nameof(FeedViewModel.IsCompactMessageWrap) or nameof(FeedViewModel.IsCompactSuperChatWrap))
            {
                scroll?.NotifyLayoutChanged();
            }
        };
        Content = surface;
        UpdateColumnRatio();
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        runtime.Changed += OnRuntimeChanged;
        runtime.MessageColumnsChanged += OnMessageColumnsChanged;
        UpdateBorderThickness();
        UpdateColumnRatio();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CancelColumnResizes();
        ScheduleColumnWidthSave();
        runtime.Changed -= OnRuntimeChanged;
        runtime.MessageColumnsChanged -= OnMessageColumnsChanged;
        EmoticonBitmaps.Clear();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnRuntimeChanged(string roomId)
    {
        if (roomId.Length == 0)
        {
            UpdateBorderThickness();
            CancelColumnResizes();
            UpdateColumnRatio();
        }
    }

    private void OnMessageColumnsChanged(MessageKind changedKind)
    {
        if (changedKind == messageKind)
        {
            UpdateColumnRatio();
        }
    }

    private void UpdateBorderThickness()
    {
        var thickness = new Thickness(runtime.Configuration.Settings.MessageBorders.GetThickness(messageKind));
        if (Resources.ContainsKey("MessageCardBorderThickness") && thickness.Equals(Resources["MessageCardBorderThickness"]))
        {
            return;
        }
        scroll?.BeginViewportUpdate();
        try
        {
            Resources["MessageCardBorderThickness"] = thickness;
            scroll?.NotifyLayoutChanged();
        }
        finally
        {
            scroll?.EndViewportUpdate();
        }
    }

    private void UpdateColumnRatio()
    {
        if (messageKind == MessageKind.SuperChat)
        {
            return;
        }
        var widths = runtime.Configuration.Settings.MessageColumns.GetWidths(messageKind);
        var ratio = Model.IsCompactDisplay ? widths.CompactIdentityRatio : widths.RegularIdentityRatio;
        if (GetValue(MessageFeedColumnLayout.IdentityRatioProperty) is { } current && current.Equals(ratio))
        {
            return;
        }
        scroll?.BeginViewportUpdate();
        try
        {
            SetValue(MessageFeedColumnLayout.IdentityRatioProperty, ratio);
            scroll?.NotifyLayoutChanged();
        }
        finally
        {
            scroll?.EndViewportUpdate();
        }
    }

    private void OnColumnResizeRequested(object? sender, MessageColumnResizeEventArgs args)
    {
        args.Handled = true;
        if (args.IsCompleted)
        {
            ScheduleColumnWidthSave();
            return;
        }
        var widths = runtime.Configuration.Settings.MessageColumns.GetWidths(messageKind);
        var previous = Model.IsCompactDisplay ? widths.CompactIdentityRatio : widths.RegularIdentityRatio;
        if (previous.Equals(args.Ratio))
        {
            return;
        }
        if (Model.IsCompactDisplay)
        {
            widths.CompactIdentityRatio = args.Ratio;
        }
        else
        {
            widths.RegularIdentityRatio = args.Ratio;
        }
        columnWidthsDirty = true;
        columnWidthRevision++;
        runtime.NotifyMessageColumnsChanged(messageKind);
    }

    private void OnColumnResizeStarted(object? sender, MessageColumnResizeStartedEventArgs args)
    {
        args.Handled = true;
        columnResize.Begin(args);
    }

    private void CancelColumnResizes()
    {
        columnResize.Cancel();
    }

    private void ScheduleColumnWidthSave()
    {
        if (!columnWidthsDirty || scheduledColumnWidthRevision == columnWidthRevision)
        {
            return;
        }
        var revision = columnWidthRevision;
        scheduledColumnWidthRevision = revision;
        PendingColumnWidthSave = RunAsync(async () =>
        {
            try
            {
                await runtime.ConfigurationStore.SaveAsync(runtime.Configuration);
            }
            catch
            {
                if (scheduledColumnWidthRevision == revision)
                {
                    scheduledColumnWidthRevision = -1;
                }
                throw;
            }
            if (columnWidthRevision == revision)
            {
                columnWidthsDirty = false;
            }
        });
    }

    private void UpdateTextScale()
    {
        var scale = Model.MessageTextScale;
        SetValue(EmoticonTextBlock.EmoticonHeightProperty, EmoticonTextBlock.DEFAULT_EMOTICON_HEIGHT * scale);
        Resources["MessageFontSizeBody"] = 13 * scale;
        Resources["MessageFontSizeCompact"] = 12 * scale;
        Resources["MessageFontSizeMetadata"] = 11 * scale;
        Resources["MessageFontSizePinyin"] = 10 * scale;
        Resources["MessageMedalSlotWidth"] = 66 * scale;
        Resources["MessageMedalLevelWidth"] = 20 * scale;
        Resources["MessagePinBadgeSize"] = 14 * scale;
        Resources["MessageUserAvatarSize"] = 20 * scale;
        Resources["MessageUserAvatarCompactSize"] = 16 * scale;
    }

    private void OnInitialLoadCompleted()
    {
        if (scroll is not null)
        {
            scroll.CompleteInitialLoad();
            UpdateReadingState(scroll.IsFollowingLatest);
        }
    }

    private void UpdateReadingState(bool followingLatest)
    {
        Model.IsReadingHistory = Model.ScrollingItems.Count > 0 && !followingLatest;
    }

    private void OnViewportUpdateStarted()
    {
        scroll?.BeginViewportUpdate();
    }

    private void OnViewportUpdateCompleted()
    {
        scroll?.EndViewportUpdate();
    }

    private async Task ClearAsync(MessageKind kind)
    {
        var room = Model.SelectedRoom;
        if (room is null || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }
        var dialog = new ConfirmationWindow(Localization.Get("Panel.ClearFeed"),
            Localization.Format("Panel.ClearFeedConfirm", room.Name, Localization.Get("Workbench.Panel." + kind)));
        if (await dialog.ShowDialog<bool>(owner))
        {
            await Model.ClearAsync(room);
        }
    }

    private ListBox CreateList(IEnumerable<FeedRowViewModel> items, MessageKind kind, string name)
    {
        var result = new MessageFeedListBox(() => CreateRow(kind), kind)
        {
            Name = name,
            ItemsSource = items,
            BorderThickness = new Thickness(0), Background = Brushes.Transparent
        };
        if (kind == MessageKind.Danmu)
        {
            result.Classes.Add("danmuFeedList");
        }
        result.ContextMenu = (LocalizedContextMenu)GetTemplate(kind == MessageKind.Danmu ? "DanmuPanelMenu" : "CardPanelMenu").Build(Header)!;
        return result;
    }

    private void AttachScroll(ListBox list, MessageKind kind)
    {
        if (list.GetVisualRoot() is not null && scroll is null &&
            list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } viewer)
        {
            scroll = new(viewer, list, followNewMessages: kind == MessageKind.Danmu, startAtTop: kind != MessageKind.Danmu);
            scroll.FollowingLatestChanged += UpdateReadingState;
            scroll.Attach(Model.ScrollingRows);
            UpdateReadingState(scroll.IsFollowingLatest);
            for (var index = 0; index < Model.ViewportUpdateDepth; index++)
            {
                scroll.BeginViewportUpdate();
            }
            if (isScrollToTopPending)
            {
                isScrollToTopPending = false;
                scroll.ScrollToTop();
            }
        }
    }

    private MessageFeedRow CreateRow(MessageKind kind)
    {
        var row = new MessageFeedRow(kind, runtime.UserAvatars, EmoticonBitmaps);
        row.Click += async (_, args) =>
        {
            args.Handled = true;
            if (row.DataContext is MessageItemViewModel item)
            {
                await RunAsync(() => ActivateMessageAsync(item));
            }
        };
        row.CopyRequested += (_, _) => ShowCopyPopup(row);
        row.ContextMenu = CreateMessageMenu(row, kind);
        return row;
    }

    internal void ShowCopyPopup(MessageFeedRow row)
    {
        if (row.DataContext is not MessageItemViewModel { IsEventSummary: false } item)
        {
            return;
        }
        var anchorBounds = new Rect(row.TranslatePoint(default, this)!.Value, row.Bounds.Size);
        row.ContextMenu?.Close();
        CopyPopup?.Hide();
        var popup = new MessageCopyPopup(item.Record.Content, item.HasTranslation ? item.Translation : "", reportError);
        CopyPopup = popup;
        popup.Closed += (_, _) => CopyPopup = null;
        popup.Show(this, anchorBounds);
    }

    private IDataTemplate GetTemplate(string key)
    {
        return new ContextBoundDataTemplate((IDataTemplate)templates[key]!);
    }

    internal Task SelectReadTabAsync(int index)
    {
        return RunAsync(() => Model.SelectReadFilterAsync(index == 0 ? MessageReadFilter.Unread : MessageReadFilter.Read));
    }

    private LocalizedContextMenu CreateMessageMenu(MessageFeedRow row, MessageKind kind)
    {
        var model = new MessageFeedMenuViewModel(kind, Header, () => row.DataContext as MessageItemViewModel,
            ActivateMessageAsync, _ =>
            {
                ShowCopyPopup(row);
                return Task.CompletedTask;
            }, RetryTranslationAsync, async item =>
            {
                var followed = runtime.Configuration.Settings.SpecialAttention.Users.Any(user => user.UserUid == item.UserUid);
                await runtime.SetSpecialAttentionAsync(item.UserUid, item.Name, !followed);
            }, item => runtime.Configuration.Settings.Translation.Enabled && !runtime.IsTranslationBlocked(item.Record),
            item => runtime.Configuration.Settings.SpecialAttention.Users.Any(user => user.UserUid == item.UserUid), reportError);
        var key = kind switch
        {
            MessageKind.Danmu => "DanmuRowMenu",
            MessageKind.SuperChat => "SuperChatRowMenu",
            _ => "CardRowMenu"
        };
        var menu = (LocalizedContextMenu)GetTemplate(key).Build(model)!;
        menu.Opened += (_, _) =>
        {
            model.Refresh();
            Localization.LanguageChanged += OnMenuLanguageChanged;
        };
        menu.Closed += (_, _) => Localization.LanguageChanged -= OnMenuLanguageChanged;
        return menu;

        void OnMenuLanguageChanged(object? sender, EventArgs args)
        {
            model.Refresh();
        }
    }

    private async Task RetryTranslationAsync(MessageItemViewModel item)
    {
        if (item.IsEventSummary)
        {
            return;
        }
        await runtime.Messages.RetryTranslationJobAsync(item.Record.Id, item.Record.Generation,
            TranslationRequestFactory.GetConfigurationFingerprint(runtime.Configuration.Settings.Translation));
        runtime.NotifyTranslation(retryAuthentication: true);
        await Model.RefreshAsync();
    }

    private async Task ActivateMessageAsync(MessageItemViewModel item)
    {
        if (item.IsEventSummary)
        {
            return;
        }
        var record = item.Record;
        if (record.Kind != MessageKind.Danmu && record.IsRead)
        {
            return;
        }
        if (record.Kind == MessageKind.Danmu || !record.IsRead)
        {
            scroll?.PrepareForItemMove(item);
        }
        try
        {
            if (record.Kind == MessageKind.Danmu)
            {
                var updated = await runtime.Messages.TogglePinAsync(record.Id);
                if (updated?.IsPinned == true && runtime.Configuration.Settings.Translation.Enabled &&
                    !runtime.IsTranslationBlocked(updated))
                {
                    await runtime.Messages.UpsertTranslationJobAsync(updated.Id, updated.Generation,
                        TranslationRequestFactory.GetConfigurationFingerprint(runtime.Configuration.Settings.Translation));
                }
            }
            else
            {
                await runtime.Messages.MarkReadAsync(record.Id, record.Quantity, record.TotalPrice);
            }
            runtime.NotifyTranslation();
            await Model.RefreshAsync();
        }
        finally
        {
            scroll?.CompleteItemMove();
        }
    }

    private Task ScrollToTopAsync()
    {
        if (scroll is not null)
        {
            scroll.ScrollToTop();
        }
        else
        {
            isScrollToTopPending = true;
            if (Model.ScrollingItems.Count > 0)
            {
                list.ScrollIntoView(Model.ScrollingItems[0]);
            }
        }
        Model.IsReadingHistory = true;
        return Task.CompletedTask;
    }

    private Task ScrollToLatestAsync()
    {
        isScrollToTopPending = false;
        if (scroll is not null)
        {
            scroll.ScrollToLatest();
        }
        else
        {
            Model.IsReadingHistory = false;
            if (Model.ScrollingItems.Count > 0)
            {
                list.ScrollIntoView(Model.ScrollingItems[^1]);
            }
        }
        if (Model.PinnedItems.Count > 0)
        {
            pinnedList.ScrollIntoView(Model.PinnedItems[0]);
        }
        return Task.CompletedTask;
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            reportError(exception);
        }
    }

}
