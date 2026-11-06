using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Lucide.Avalonia;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Views;

/// <summary>使用虚拟化容器展示可点击的消息行。</summary>
public sealed class MessageFeedView : UserControl, IWorkbenchHeaderActions, IWorkbenchHeaderBadge
{
    private readonly PanelRuntime runtime;
    private readonly Action<Exception> reportError;
    private readonly MessageKind messageKind;
    private readonly ListBox list;
    private readonly ListBox pinnedList;
    private DanmuFeedScrollController? scroll;
    private bool isScrollToTopPending;
    private bool columnWidthsDirty;
    private int columnWidthRevision;
    private int scheduledColumnWidthRevision = -1;
    internal Task PendingColumnWidthSave { get; private set; } = Task.CompletedTask;
    internal MessageCopyPopup? CopyPopup { get; private set; }
    public FeedViewModel Model { get; }

    /// <summary>为每个停靠或浮动宿主创建独立的面板操作按钮。</summary>
    public IDataTemplate HeaderActionsTemplate { get; }

    /// <inheritdoc />
    public IDataTemplate? HeaderBadgeTemplate { get; }

    /// <summary>创建指定消息面板。</summary>
    public MessageFeedView(PanelRuntime runtime, MessageKind kind, Action<Exception> reportError)
    {
        this.runtime = runtime;
        this.reportError = reportError;
        messageKind = kind;
        Model = new(runtime, kind);
        AddHandler(MessageFeedColumnLayout.ResizeRequestedEvent, OnColumnResizeRequested);
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
        HeaderActionsTemplate = new FuncDataTemplate<MessageFeedView>((_, _) =>
        {
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            var clear = Ui.IconButton("Panel.ClearFeed", LucideIconKind.Trash2, () => ClearAsync(kind), reportError);
            clear.Name = "ClearFeedButton";
            clear.Classes.Add("dockChromeButton");
            clear.Bind(IsEnabledProperty, new Binding(nameof(FeedViewModel.HasSelectedRoom)) { Source = Model });
            actions.Children.Add(clear);
            var button = Ui.IconButton("Panel.BackToLatest", LucideIconKind.ArrowDownToLine, ScrollToLatestAsync, reportError);
            button.Name = "BackToLatestButton";
            button.Classes.Add("dockChromeButton");
            if (kind != MessageKind.Danmu)
            {
                var markAllRead = Ui.IconButton("Panel.MarkAllRead", LucideIconKind.CheckCheck, Model.MarkAllReadAsync, reportError);
                markAllRead.Name = "MarkAllReadButton";
                markAllRead.Classes.Add("dockChromeButton");
                markAllRead.Bind(IsEnabledProperty, new Binding(nameof(FeedViewModel.HasUnreadMessages)) { Source = Model });
                actions.Children.Add(markAllRead);
                var top = Ui.IconButton("Panel.BackToTop", LucideIconKind.ArrowUpToLine, ScrollToTopAsync, reportError);
                top.Name = "BackToTopButton";
                top.Classes.Add("dockChromeButton");
                top.Bind(IsEnabledProperty, new Binding(nameof(FeedViewModel.HasSelectedRoom)) { Source = Model });
                actions.Children.Add(top);
            }
            actions.Children.Add(button);
            return actions;
        });
        if (kind is MessageKind.SuperChat or MessageKind.Gift or MessageKind.Guard)
        {
            HeaderBadgeTemplate = new FuncDataTemplate<MessageFeedView>((_, _) => CreateUnreadBadge());
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
            if (args.PropertyName == nameof(FeedViewModel.SelectedRoom))
            {
                isScrollToTopPending = false;
                CancelColumnResizes();
                CopyPopup?.Hide();
            }
            else if (args.PropertyName == nameof(FeedViewModel.HasScrollingItems))
            {
                UpdatePinnedHeight();
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

    private void CancelColumnResizes()
    {
        foreach (var layout in this.GetVisualDescendants().OfType<MessageFeedColumnLayout>())
        {
            layout.CancelResize();
        }
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
        Resources["MessageFontSizeBody"] = 13 * scale;
        Resources["MessageFontSizeCompact"] = 12 * scale;
        Resources["MessageFontSizeMetadata"] = 11 * scale;
        Resources["MessageFontSizePinyin"] = 10 * scale;
        Resources["MessageMedalSlotWidth"] = 66 * scale;
        Resources["MessageMedalLevelWidth"] = 20 * scale;
        Resources["MessagePinBadgeSize"] = 14 * scale;
    }

    private void OnInitialLoadCompleted()
    {
        scroll?.CompleteInitialLoad();
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
        result.Styles.Add(new Avalonia.Styling.Style(selector => selector.OfType<ListBoxItem>())
        {
            Setters = { new Setter(PaddingProperty, new Thickness(0)), new Setter(MarginProperty, new Thickness(0)) }
        });
        var panelMenu = new LocalizedContextMenu();
        AddPanelActions(panelMenu);
        result.ContextMenu = panelMenu;
        return result;
    }

    private void AttachScroll(ListBox list, MessageKind kind)
    {
        if (list.GetVisualRoot() is not null && scroll is null &&
            list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } viewer)
        {
            scroll = new(viewer, list, followNewMessages: kind == MessageKind.Danmu);
            scroll.FollowingLatestChanged += following => Model.IsReadingHistory = !following;
            scroll.Attach(Model.ScrollingRows);
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
        var row = new MessageFeedRow(kind);
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
        if (row.DataContext is not MessageItemViewModel item)
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

    private Border CreateUnreadBadge()
    {
        var count = new TextBlock();
        count.Bind(TextBlock.TextProperty, new Binding(nameof(FeedViewModel.UnreadCount)) { Source = Model });
        count.Bind(TextBlock.FontSizeProperty, new Binding(nameof(FeedViewModel.MessageTextScale))
        {
            Source = Model,
            Converter = new FuncValueConverter<double, double>(scale => 13 * scale)
        });
        var badge = new Border { Child = count, IsHitTestVisible = false };
        badge.Classes.Add("panelUnreadBadge");
        badge.Bind(IsVisibleProperty, new Binding(nameof(FeedViewModel.HasUnreadMessages)) { Source = Model });
        return badge;
    }

    private LocalizedContextMenu CreateMessageMenu(MessageFeedRow row, MessageKind kind)
    {
        var menu = new LocalizedContextMenu();
        var activation = AddAction(menu, kind == MessageKind.Danmu ? "Panel.PinMessage" : "Panel.MarkRead",
            kind == MessageKind.Danmu ? LucideIconKind.Pin : LucideIconKind.Check,
            () => row.DataContext is MessageItemViewModel item ? ActivateMessageAsync(item) : Task.CompletedTask);
        void RefreshActivation()
        {
            if (row.DataContext is not MessageItemViewModel item)
            {
                return;
            }
            if (kind == MessageKind.Danmu)
            {
                menu.SetHeader(activation, item.Record.IsPinned ? "Panel.UnpinMessage" : "Panel.PinMessage");
                ((LucideIcon)activation.Icon!).Kind = item.Record.IsPinned ? LucideIconKind.PinOff : LucideIconKind.Pin;
            }
            else
            {
                activation.IsEnabled = !item.Record.IsRead;
            }
        }
        RefreshActivation();
        menu.Opened += (_, _) => RefreshActivation();
        if (kind is MessageKind.Danmu or MessageKind.SuperChat)
        {
            AddAction(menu, "Panel.CopyText", LucideIconKind.Copy, () =>
            {
                ShowCopyPopup(row);
                return Task.CompletedTask;
            });
            var retry = AddAction(menu, "Panel.RetryTranslation", LucideIconKind.Languages, async () =>
            {
                if (row.DataContext is MessageItemViewModel item && runtime.Configuration.Settings.Translation.Enabled)
                {
                    await runtime.Messages.RetryTranslationJobAsync(item.Record.Id, item.Record.Generation,
                        TranslationRequestFactory.GetConfigurationFingerprint(runtime.Configuration.Settings.Translation));
                    runtime.NotifyTranslation(retryAuthentication: true);
                    await Model.RefreshAsync();
                }
            });
            retry.IsEnabled = runtime.Configuration.Settings.Translation.Enabled;
            menu.Opened += (_, _) => retry.IsEnabled = runtime.Configuration.Settings.Translation.Enabled;
        }
        menu.Items.Add(new Separator());
        AddPanelActions(menu);
        return menu;
    }

    private void AddPanelActions(LocalizedContextMenu menu)
    {
        AddAction(menu, "Panel.MoreHistory", LucideIconKind.History, Model.LoadMoreAsync);
        if (messageKind is MessageKind.SuperChat or MessageKind.Gift or MessageKind.Guard)
        {
            AddAction(menu, "Panel.BackToTop", LucideIconKind.ArrowUpToLine, ScrollToTopAsync);
        }
        AddAction(menu, "Panel.BackToLatest", LucideIconKind.ArrowDownToLine, ScrollToLatestAsync);
    }

    private MenuItem AddAction(LocalizedContextMenu menu, string key, LucideIconKind icon, Func<Task> action)
    {
        var item = new MenuItem { Icon = new LucideIcon { Kind = icon, Size = 16 } };
        menu.SetHeader(item, key);
        item.Click += async (_, args) =>
        {
            args.Handled = true;
            item.IsEnabled = false;
            try
            {
                await RunAsync(action);
            }
            finally
            {
                item.IsEnabled = true;
            }
        };
        menu.Items.Add(item);
        return item;
    }

    private async Task ActivateMessageAsync(MessageItemViewModel item)
    {
        var record = item.Record;
        if (record.Kind == MessageKind.Danmu || !record.IsRead)
        {
            scroll?.PrepareForItemMove(item);
        }
        try
        {
            if (record.Kind == MessageKind.Danmu)
            {
                var updated = await runtime.Messages.TogglePinAsync(record.Id);
                if (updated?.IsPinned == true && runtime.Configuration.Settings.Translation.Enabled)
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
            Model.IsReadingHistory = true;
            if (Model.ScrollingItems.Count > 0)
            {
                list.ScrollIntoView(Model.ScrollingItems[0]);
            }
        }
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
