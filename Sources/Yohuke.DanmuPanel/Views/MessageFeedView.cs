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
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Views;

/// <summary>使用虚拟化容器展示可点击的消息行。</summary>
public sealed class MessageFeedView : UserControl, IWorkbenchHeaderActions
{
    private readonly PanelRuntime runtime;
    private readonly Action<Exception> reportError;
    private readonly ListBox list;
    private readonly ListBox pinnedList;
    private DanmuFeedScrollController? scroll;
    public FeedViewModel Model { get; }

    /// <summary>为每个停靠或浮动宿主创建独立的回顶图标按钮。</summary>
    public IDataTemplate HeaderActionsTemplate { get; }

    /// <summary>创建指定消息面板。</summary>
    public MessageFeedView(PanelRuntime runtime, MessageKind kind, Action<Exception> reportError)
    {
        this.runtime = runtime;
        this.reportError = reportError;
        Model = new(runtime, kind);
        Model.Failed += reportError;
        list = CreateList(Model.ScrollingItems, kind, "ScrollingMessages");
        pinnedList = CreateList(Model.PinnedItems, kind, "PinnedMessages");
        pinnedList.MaxHeight = 0;
        list.TemplateApplied += (_, _) => AttachScroll(list);
        list.AttachedToVisualTree += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => AttachScroll(list));
        list.DetachedFromVisualTree += (_, _) => { scroll?.Dispose(); scroll = null; };
        HeaderActionsTemplate = new FuncDataTemplate<MessageFeedView>((_, _) =>
        {
            var button = Ui.IconButton("Panel.BackToTop", LucideIconKind.ArrowUpToLine, ScrollToLatestAsync, reportError);
            button.Classes.Add("dockChromeButton");
            return button;
        });
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
        };
        Content = surface;
    }

    private ListBox CreateList(IEnumerable<MessageItemViewModel> items, MessageKind kind, string name)
    {
        var result = new ListBox
        {
            Name = name,
            ItemsSource = items,
            ItemTemplate = new FuncDataTemplate<MessageItemViewModel>((item, _) => item is null ? new Border() : CreateRow(item)),
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

    private void AttachScroll(ListBox list)
    {
        if (scroll is null && list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } viewer)
        {
            scroll = new(viewer, list);
            scroll.FollowingLatestChanged += following => Model.IsReadingHistory = !following;
            scroll.Attach(Model.ScrollingItems);
        }
    }

    private Control CreateRow(MessageItemViewModel item)
    {
        var identity = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };
        var time = BoundText(nameof(MessageItemViewModel.Time));
        time.Classes.Add("danmuFeedMeta");
        time.Opacity = 0.6;
        DockPanel.SetDock(time, Avalonia.Controls.Dock.Right);
        identity.Children.Add(time);
        var state = BoundText(nameof(MessageItemViewModel.State));
        state.Margin = new Thickness(0, 0, 5, 0);
        identity.Children.Add(state);
        var testMarker = Ui.Text("TestMessages.Marker");
        testMarker.Classes.Add("muted");
        testMarker.FontSize = 10;
        testMarker.Margin = new Thickness(0, 0, 4, 0);
        testMarker.Bind(IsVisibleProperty, new Binding(nameof(MessageItemViewModel.IsTest)));
        identity.Children.Add(testMarker);
        var medal = BoundText(nameof(MessageItemViewModel.MedalName));
        medal.Classes.Add("danmuMedalText");
        var level = BoundText(nameof(MessageItemViewModel.MedalLevel));
        level.Classes.Add("danmuMedalText");
        level.Classes.Add("danmuMedalLevel");
        var medalLayout = new Grid { ColumnDefinitions = new("*,Auto"), Children = { medal, level } };
        medalLayout.Classes.Add("danmuMedalLayout");
        Grid.SetColumn(level, 1);
        var medalFrame = new Border { Child = medalLayout };
        medalFrame.Classes.Add("danmuMedalBadge");
        medalFrame.Bind(IsVisibleProperty, new Binding(nameof(MessageItemViewModel.HasMedal)));
        medalFrame.Bind(Border.BorderBrushProperty, new Binding(nameof(MessageItemViewModel.MedalBorderBrush)));
        medalFrame.Bind(ToolTip.TipProperty, new Binding(nameof(MessageItemViewModel.Medal)));
        var noMedal = Ui.Text("Panel.NoMedal");
        noMedal.Classes.Add("danmuMedalText");
        noMedal.Classes.Add("danmuMedalPlaceholder");
        noMedal.Bind(IsVisibleProperty, new Binding(nameof(MessageItemViewModel.HasMedal))
        {
            Converter = BoolConverters.Not
        });
        var medalSlot = new Grid { Children = { medalFrame, noMedal } };
        medalSlot.Classes.Add("danmuMedalSlot");
        identity.Children.Add(medalSlot);
        var name = BoundText(nameof(MessageItemViewModel.Name));
        name.Classes.Add("danmuFeedUser");
        name.TextAlignment = TextAlignment.Center;
        name.Bind(ToolTip.TipProperty, new Binding(nameof(MessageItemViewModel.Name)));
        var namePinyin = BoundPinyin(nameof(MessageItemViewModel.NamePinyin), nameof(MessageItemViewModel.HasNamePinyin));
        identity.Children.Add(new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children = { namePinyin, name }
        });
        identity.Classes.Add("danmuFeedIdentity");
        var original = new EmoticonTextBlock { TextWrapping = TextWrapping.Wrap };
        original.Classes.Add("danmuFeedContent");
        original.Classes.Add("regularFeedContent");
        original.Bind(EmoticonTextBlock.ContentTextProperty, new Binding(nameof(MessageItemViewModel.Detail)));
        original.Bind(EmoticonTextBlock.EmoticonsProperty, new Binding(nameof(MessageItemViewModel.Emoticons)));
        var translation = BoundText(nameof(MessageItemViewModel.Translation));
        translation.Classes.Add("danmuFeedContent");
        translation.Classes.Add("regularFeedContent");
        translation.TextWrapping = TextWrapping.Wrap;
        translation.Opacity = 0.85;
        var originalMarquee = CreateMarquee(nameof(MessageItemViewModel.Detail));
        originalMarquee.Bind(HoverMarqueeTextBlock.EmoticonsProperty, new Binding(nameof(MessageItemViewModel.Emoticons)));
        var translationMarquee = CreateMarquee(nameof(MessageItemViewModel.Translation));
        translationMarquee.Opacity = 0.85;
        var originalRegion = new Grid { Children = { original, originalMarquee } };
        originalRegion.Bind(IsVisibleProperty, new Binding(nameof(MessageItemViewModel.ShowOriginal)));
        var translationRegion = new Grid { Children = { translation, translationMarquee } };
        translationRegion.Classes.Add("translationLine");
        translationRegion.Bind(IsVisibleProperty, new Binding(nameof(MessageItemViewModel.HasTranslation)));
        var price = BoundText(nameof(MessageItemViewModel.Price));
        price.Classes.Add("scFeedPrice");
        price.HorizontalAlignment = HorizontalAlignment.Right;
        var detailPinyin = BoundPinyin(nameof(MessageItemViewModel.DetailPinyin), nameof(MessageItemViewModel.HasDetailPinyin));
        detailPinyin.TextAlignment = TextAlignment.Left;
        var body = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children = { detailPinyin, originalRegion, translationRegion }
        };
        body.Classes.Add("messageBody");
        var layout = new Grid();
        if (item.Record.Kind == MessageKind.SuperChat)
        {
            layout.RowDefinitions = new("Auto,Auto");
            layout.Classes.Add("scFeedLayout");
            var header = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
            header.Children.Add(identity);
            Grid.SetColumn(price, 1);
            header.Children.Add(price);
            layout.Children.Add(header);
            Grid.SetRow(body, 1);
            layout.Children.Add(body);
        }
        else
        {
            layout.ColumnDefinitions = new("2*,3*");
            layout.Children.Add(identity);
            Grid.SetColumn(body, 1);
            layout.Children.Add(body);
            layout.Classes.Add("danmuFeedLayout");
        }
        var frame = new Border
        {
            BorderThickness = new Thickness(item.Record.Kind == MessageKind.SuperChat ? 2 : 0),
            CornerRadius = new CornerRadius(5), Child = layout
        };
        if (item.Record.Kind == MessageKind.SuperChat)
        {
            frame.Classes.Add("scFeedCard");
        }
        frame.Bind(Border.BorderBrushProperty, new Binding(nameof(MessageItemViewModel.BorderBrush)));
        if (item.Record.Kind == MessageKind.Guard)
        {
            frame.BorderThickness = new Thickness(3, 0, 0, 0);
        }
        var button = new Button
        {
            DataContext = item, Content = frame,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        button.Classes.Add(item.Record.Kind == MessageKind.SuperChat ? "scFeedButton" : "danmuFeedButton");
        button.Click += async (_, args) =>
        {
            args.Handled = true;
            await RunAsync(() => ActivateMessageAsync(item));
        };
        button.ContextMenu = CreateMessageMenu(item);
        return button;
    }

    private static HoverMarqueeTextBlock CreateMarquee(string property)
    {
        var text = new HoverMarqueeTextBlock();
        text.Classes.Add("compactFeedContent");
        text.Bind(HoverMarqueeTextBlock.TextProperty, new Binding(property));
        return text;
    }

    private LocalizedContextMenu CreateMessageMenu(MessageItemViewModel item)
    {
        var menu = new LocalizedContextMenu();
        var activation = AddAction(menu, item.Record.Kind == MessageKind.Danmu ? "Panel.PinMessage" : "Panel.MarkRead",
            item.Record.Kind == MessageKind.Danmu ? LucideIconKind.Pin : LucideIconKind.Check,
            () => ActivateMessageAsync(item));
        void RefreshActivation()
        {
            if (item.Record.Kind == MessageKind.Danmu)
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
        if (!item.Record.IsTest && item.Record.Kind is MessageKind.Danmu or MessageKind.SuperChat)
        {
            var retry = AddAction(menu, "Panel.RetryTranslation", LucideIconKind.Languages, async () =>
            {
                if (runtime.Configuration.Settings.Translation.Enabled)
                {
                    await runtime.Messages.UpsertTranslationJobAsync(item.Record.Id, item.Record.Generation,
                        TranslationRequestFactory.GetConfigurationFingerprint(runtime.Configuration.Settings.Translation));
                    runtime.NotifyTranslation(retryAuthentication: true);
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
        AddAction(menu, "Panel.BackToTop", LucideIconKind.ArrowUpToLine, ScrollToLatestAsync);
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
        if (record.Kind == MessageKind.Danmu)
        {
            var updated = await runtime.Messages.TogglePinAsync(record.Id);
            if (updated?.IsPinned == true && !updated.IsTest && runtime.Configuration.Settings.Translation.Enabled)
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
            scroll?.ScrollToLatest();
            list.ScrollIntoView(Model.ScrollingItems[0]);
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

    private static TextBlock BoundPinyin(string property, string visibilityProperty)
    {
        var text = BoundText(property);
        text.TextTrimming = TextTrimming.None;
        text.Classes.Add("danmuFeedPinyin");
        text.Bind(IsVisibleProperty, new Binding(visibilityProperty));
        return text;
    }

    private static TextBlock BoundText(string property)
    {
        var text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        text.Bind(TextBlock.TextProperty, new Binding(property));
        return text;
    }
}
