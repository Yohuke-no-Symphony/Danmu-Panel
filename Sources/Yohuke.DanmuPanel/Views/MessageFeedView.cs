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
    private readonly ListBox list;
    private readonly ListBox pinnedList;
    private DanmuFeedScrollController? scroll;
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
        Model = new(runtime, kind);
        UpdateTextScale();
        Model.Failed += reportError;
        list = CreateList(Model.ScrollingItems, kind, "ScrollingMessages");
        pinnedList = CreateList(Model.PinnedItems, kind, "PinnedMessages");
        pinnedList.MaxHeight = 0;
        list.TemplateApplied += (_, _) => AttachScroll(list, kind);
        list.AttachedToVisualTree += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => AttachScroll(list, kind));
        list.DetachedFromVisualTree += (_, _) => { scroll?.Dispose(); scroll = null; };
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
            if (args.PropertyName == nameof(FeedViewModel.HasScrollingItems))
            {
                UpdatePinnedHeight();
            }
            else if (args.PropertyName == nameof(FeedViewModel.MessageTextScale))
            {
                UpdateTextScale();
                scroll?.NotifyLayoutChanged();
            }
            else if (args.PropertyName is nameof(FeedViewModel.IsCompactDisplay) or
                     nameof(FeedViewModel.IsCompactMessageWrap) or nameof(FeedViewModel.IsCompactSuperChatWrap))
            {
                scroll?.NotifyLayoutChanged();
            }
        };
        Content = surface;
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

    private ListBox CreateList(IEnumerable<MessageItemViewModel> items, MessageKind kind, string name)
    {
        var result = new MessageFeedListBox(() => CreateRow(kind))
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
        if (scroll is null && list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } viewer)
        {
            scroll = new(viewer, list, followNewMessages: kind == MessageKind.Danmu);
            scroll.FollowingLatestChanged += following => Model.IsReadingHistory = !following;
            scroll.Attach(Model.ScrollingItems);
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
        row.ContextMenu = CreateMessageMenu(row, kind);
        return row;
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
            var retry = AddAction(menu, "Panel.RetryTranslation", LucideIconKind.Languages, async () =>
            {
                if (row.DataContext is MessageItemViewModel item && runtime.Configuration.Settings.Translation.Enabled)
                {
                    await runtime.Messages.UpsertTranslationJobAsync(item.Record.Id, item.Record.Generation,
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

    private Task ScrollToLatestAsync()
    {
        if (Model.ScrollingItems.Count > 0)
        {
            if (scroll is not null)
            {
                scroll.ScrollToLatest();
            }
            else
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
