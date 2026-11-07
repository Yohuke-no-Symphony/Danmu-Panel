using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.ViewModels;

namespace Yohuke.DanmuPanel.Views.Components;

[PseudoClasses(":alternate", ":specialAttention")]
internal sealed class MessageFeedRow : Button
{
    private static readonly StyledProperty<bool> IsSpecialAttentionProperty =
        AvaloniaProperty.Register<MessageFeedRow, bool>(nameof(MessageItemViewModel.IsSpecialAttention));
    private static IMultiValueConverter InlineTranslationConverter { get; } =
        new FuncMultiValueConverter<string, bool>(texts => texts.All(text => text is not null && text.IndexOfAny(['\r', '\n']) < 0));
    private readonly bool canCopy;
    private bool isCopyHoldEligible;
    private bool suppressClick;
    private Point copyPressPosition;
    private IPointer? copyPointer;

    internal event EventHandler? CopyRequested;

    /// <inheritdoc/>
    protected override Type StyleKeyOverride => typeof(Button);

    internal void UpdateAlternatingBackground(int index)
    {
        PseudoClasses.Set(":alternate", (index & 1) != 0);
    }

    internal MessageFeedRow(MessageKind kind, IUserAvatarService? avatars = null, EmoticonBitmapCache? bitmapCache = null)
    {
        this.Bind(IsSpecialAttentionProperty, new Binding(nameof(MessageItemViewModel.IsSpecialAttention)));
        canCopy = kind is MessageKind.Danmu or MessageKind.SuperChat;
        Gestures.SetIsHoldingEnabled(this, canCopy);
        Gestures.SetIsHoldWithMouseEnabled(this, canCopy);
        Holding += OnHolding;
        var identity = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };
        var time = BoundText(nameof(MessageItemViewModel.Time));
        time.Classes.Add("danmuFeedMeta");
        DockPanel.SetDock(time, Avalonia.Controls.Dock.Right);
        identity.Children.Add(time);
        var avatar = new UserAvatarView(avatars);
        avatar.Bind(UserAvatarView.UserUidProperty, new Binding(nameof(MessageItemViewModel.UserUid)));
        avatar.Bind(UserAvatarView.ShowAvatarProperty, new Binding(nameof(MessageItemViewModel.ShowUserAvatars)));
        Grid? medalSlot = null;
        if (kind == MessageKind.Danmu)
        {
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
            var noMedalFrame = new Border { Child = noMedal };
            noMedalFrame.Classes.Add("danmuMedalPlaceholder");
            noMedalFrame.Bind(IsVisibleProperty, new Binding(nameof(MessageItemViewModel.HasMedal))
            {
                Converter = BoolConverters.Not
            });
            medalSlot = new() { Children = { medalFrame, noMedalFrame } };
            medalSlot.Classes.Add("danmuMedalSlot");
            medalSlot.Bind(IsVisibleProperty, new Binding(nameof(MessageItemViewModel.ShowFanMedals)));
        }
        var name = new EmoticonTextBlock();
        name.Bind(EmoticonTextBlock.ContentTextProperty, new Binding(nameof(MessageItemViewModel.Name)));
        name.Bind(EmoticonTextBlock.AnnotationsProperty, new Binding(nameof(MessageItemViewModel.NameAnnotations)));
        name.Bind(EmoticonTextBlock.PinyinLayoutProperty, new Binding(nameof(MessageItemViewModel.PinyinLayout)));
        name.Bind(EmoticonTextBlock.PinyinForegroundProperty, new DynamicResourceExtension("MessagePinyinBrush"));
        name.Bind(EmoticonTextBlock.PinyinFontSizeProperty, new DynamicResourceExtension("MessageFontSizePinyin"));
        name.Classes.Add("danmuFeedUser");
        name.TextAlignment = TextAlignment.Left;
        name.Bind(ToolTip.TipProperty, new Binding(nameof(MessageItemViewModel.Name)));
        var namePending = CreatePinyinPlaceholder(nameof(MessageItemViewModel.IsNamePinyinPending), 4, name);
        namePending.Classes.Add("namePinyinPending");
        namePending.HorizontalAlignment = HorizontalAlignment.Left;
        var userName = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children = { namePending, name }
        };
        var userIdentity = new Grid
        {
            ColumnDefinitions = new("Auto,*"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children = { avatar, userName }
        };
        Grid.SetColumn(userName, 1);
        identity.Children.Add(userIdentity);
        identity.Classes.Add("danmuFeedIdentity");
        var original = new EmoticonTextBlock { TextWrapping = TextWrapping.Wrap, BitmapCache = bitmapCache };
        original.Classes.Add("danmuFeedContent");
        original.Classes.Add("regularFeedContent");
        original.Classes.Add("originalText");
        original.Bind(EmoticonTextBlock.ContentTextProperty, new Binding(nameof(MessageItemViewModel.Detail)));
        original.Bind(EmoticonTextBlock.EmoticonsProperty, new Binding(nameof(MessageItemViewModel.Emoticons)));
        original.Bind(EmoticonTextBlock.AnnotationsProperty, new Binding(nameof(MessageItemViewModel.ContentPinyin)));
        original.Bind(EmoticonTextBlock.PinyinLayoutProperty, new Binding(nameof(MessageItemViewModel.PinyinLayout)));
        original.Bind(EmoticonTextBlock.PinyinForegroundProperty, new DynamicResourceExtension("MessagePinyinBrush"));
        original.Bind(EmoticonTextBlock.PinyinFontSizeProperty, new DynamicResourceExtension("MessageFontSizePinyin"));
        var translation = new MessageTranslationTextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        translation.Bind(TextBlock.TextProperty, new Binding(nameof(MessageItemViewModel.Translation)));
        translation.Classes.Add("danmuFeedContent");
        translation.Classes.Add("regularFeedContent");
        translation.Classes.Add("translatedText");
        translation.TextWrapping = TextWrapping.Wrap;
        var originalMarquee = CreateMarquee(nameof(MessageItemViewModel.Detail), bitmapCache);
        originalMarquee.Classes.Add("originalText");
        originalMarquee.Bind(HoverMarqueeTextBlock.EmoticonsProperty, new Binding(nameof(MessageItemViewModel.Emoticons)));
        originalMarquee.Bind(HoverMarqueeTextBlock.AnnotationsProperty, new Binding(nameof(MessageItemViewModel.ContentPinyin)));
        originalMarquee.Bind(HoverMarqueeTextBlock.PinyinLayoutProperty, new Binding(nameof(MessageItemViewModel.PinyinLayout)));
        originalMarquee.Bind(HoverMarqueeTextBlock.PinyinForegroundProperty, new DynamicResourceExtension("MessagePinyinBrush"));
        originalMarquee.Bind(HoverMarqueeTextBlock.PinyinFontSizeProperty, new DynamicResourceExtension("MessageFontSizePinyin"));
        var translationMarquee = CreateMarquee(nameof(MessageItemViewModel.Translation));
        translationMarquee.Classes.Add("translatedText");
        translationMarquee.Bind(TextBlock.LineHeightProperty, new Binding(nameof(TextBlock.LineHeight)) { Source = translation });
        var translatedContent = new Grid { Children = { translation, translationMarquee } };
        translatedContent.Bind(IsVisibleProperty, new Binding(nameof(MessageItemViewModel.HasTranslation)));
        var pendingTranslation = new ShimmerTextBlock { HorizontalAlignment = HorizontalAlignment.Left };
        pendingTranslation.Classes.Add("translationPending");
        pendingTranslation.Bind(ShimmerTextBlock.TextProperty, Localization.Observe("Panel.Translating"));
        pendingTranslation.Bind(IsVisibleProperty, new Binding(nameof(MessageItemViewModel.IsTranslating)));
        pendingTranslation.Bind(TextBlock.LineHeightProperty, new Binding(nameof(TextBlock.LineHeight)) { Source = translation });
        pendingTranslation.Bind(TextBlock.LineSpacingProperty, new Binding(nameof(TextBlock.LineSpacing)) { Source = translation });
        var contentPending = CreatePinyinPlaceholder(nameof(MessageItemViewModel.IsContentPinyinPending), 6, original);
        contentPending.Classes.Add("contentPinyinPending");
        var originalRegion = new StackPanel
        {
            Children = { contentPending, new Grid { Children = { original, originalMarquee } } }
        };
        originalRegion.Bind(IsVisibleProperty, new Binding(nameof(MessageItemViewModel.ShowOriginal)));
        var translationRegion = new Grid { Children = { translatedContent, pendingTranslation } };
        translationRegion.Classes.Add("translationLine");
        translationRegion.Bind(IsVisibleProperty, new Binding(nameof(MessageItemViewModel.ShowTranslation)));
        var price = BoundText(nameof(MessageItemViewModel.Price));
        price.Classes.Add("scFeedPrice");
        price.HorizontalAlignment = HorizontalAlignment.Right;
        var body = new MessageBodyPanel(originalRegion, translationRegion,
            () => contentPending.DesiredSize.Height + (original.IsVisible
                ? original.Padding.Top + original.TextLayout.Baseline : originalMarquee.TextBaseline),
            () => pendingTranslation.IsVisible ? pendingTranslation.TextBaseline : translation.IsVisible
                ? translation.Padding.Top + translation.TextLayout.Baseline : translationMarquee.TextBaseline,
            () => original.IsVisible ? original.InlineTextWidth : originalMarquee.InlineTextWidth,
            () => (original.IsVisible ? original.IsSingleTextLine : originalMarquee.IsSingleTextLine) &&
                (pendingTranslation.IsVisible ? pendingTranslation.IsSingleTextLine : translation.IsVisible
                    ? EmoticonTextBlock.HasSingleTextLine(translation) : translationMarquee.IsSingleTextLine))
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        original.AnnotationLayoutChanged += () => body.InvalidateTextMetrics(original);
        originalMarquee.TextMetricsChanged += body.InvalidateTextMetrics;
        translationMarquee.TextMetricsChanged += body.InvalidateTextMetrics;
        body.ObserveText(translation);
        body.ObserveText(pendingTranslation);
        body.ObserveText(contentPending);
        if (kind == MessageKind.Danmu)
        {
            body.Bind(MessageBodyPanel.AllowInlineProperty, new MultiBinding
            {
                Converter = InlineTranslationConverter,
                Bindings =
                {
                    new Binding(nameof(MessageItemViewModel.Detail)),
                    new Binding(nameof(MessageItemViewModel.Translation))
                }
            });
        }
        body.Classes.Add("messageBody");
        var layout = MessageFeedLayout.CreateGrid(kind);
        if (kind == MessageKind.SuperChat)
        {
            var header = MessageFeedLayout.CreateHeaderGrid();
            header.Children.Add(identity);
            Grid.SetColumn(price, 1);
            header.Children.Add(price);
            layout.Children.Add(header);
            Grid.SetRow(body, 1);
            layout.Children.Add(body);
        }
        else
        {
            if (medalSlot is not null)
            {
                layout.Children.Add(medalSlot);
            }
            Grid.SetColumn(identity, 1);
            layout.Children.Add(identity);
            Grid.SetColumn(body, 3);
            layout.Children.Add(body);
        }
        var isCard = kind is MessageKind.SuperChat or MessageKind.Gift or MessageKind.Guard;
        var frame = new Border
        {
            Child = layout
        };
        if (kind == MessageKind.SuperChat)
        {
            frame.Classes.Add("scFeedCard");
        }
        if (isCard)
        {
            frame.Classes.Add("messageCard");
            frame.Bind(Border.BorderThicknessProperty, new DynamicResourceExtension("MessageCardBorderThickness"));
        }
        frame.Bind(Border.BorderBrushProperty, new Binding(nameof(MessageItemViewModel.BorderBrush)));
        var overlay = new Canvas
        {
            HorizontalAlignment = isCard ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false
        };
        if (isCard)
        {
            var dot = new Border { IsHitTestVisible = false };
            dot.Classes.Add("messageUnreadDot");
            dot.Bind(IsVisibleProperty, new Binding(nameof(MessageItemViewModel.ShowUnreadDot)));
            Canvas.SetRight(dot, 0);
            overlay.Children.Add(dot);
        }
        else
        {
            var pin = new TextBlock { Text = "📌", IsHitTestVisible = false };
            pin.Classes.Add("messagePinBadge");
            pin.Bind(IsVisibleProperty, new Binding(nameof(MessageItemViewModel.ShowPinBadge)));
            overlay.Children.Add(pin);
        }
        Content = new Grid { Children = { frame, overlay } };
        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Classes.Add(kind == MessageKind.SuperChat ? "scFeedButton" : "danmuFeedButton");
        if (isCard)
        {
            Classes.Add("messageCardButton");
        }
    }

    private static HoverMarqueeTextBlock CreateMarquee(string property, EmoticonBitmapCache? bitmapCache = null)
    {
        var text = new HoverMarqueeTextBlock { BitmapCache = bitmapCache };
        text.Classes.Add("compactFeedContent");
        text.Bind(HoverMarqueeTextBlock.TextProperty, new Binding(property));
        text.Bind(HoverMarqueeTextBlock.AutoScrollProperty, new Binding(nameof(MessageItemViewModel.AutoScrollLongMessages)));
        return text;
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        suppressClick = false;
        copyPressPosition = e.GetPosition(this);
        isCopyHoldEligible = canCopy &&
            e.GetCurrentPoint(this).Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed;
        copyPointer = isCopyHoldEligible ? e.Pointer : null;
        base.OnPointerPressed(e);
        if (isCopyHoldEligible && e.Pointer.Type == PointerType.Mouse)
        {
            e.Pointer.Capture(this);
        }
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        ReleaseCopyPointer();
        isCopyHoldEligible = false;
        suppressClick = false;
    }

    /// <inheritdoc />
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        isCopyHoldEligible = false;
        copyPointer = null;
        base.OnPointerCaptureLost(e);
    }

    /// <inheritdoc />
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        suppressClick |= copyPointer is not null;
        isCopyHoldEligible = false;
        base.OnPointerWheelChanged(e);
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (isCopyHoldEligible)
        {
            var delta = e.GetPosition(this) - copyPressPosition;
            var tolerance = TopLevel.GetTopLevel(this)!.PlatformSettings!.GetTapSize(e.Pointer.Type);
            if (Math.Abs(delta.X) > tolerance.Width || Math.Abs(delta.Y) > tolerance.Height)
            {
                isCopyHoldEligible = false;
                suppressClick = true;
            }
        }
        base.OnPointerMoved(e);
    }

    /// <inheritdoc />
    protected override void OnClick()
    {
        if (!suppressClick)
        {
            base.OnClick();
        }
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        CancelCopyInteraction();
        base.OnDataContextChanged(e);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsSpecialAttentionProperty)
        {
            PseudoClasses.Set(":specialAttention", GetValue(IsSpecialAttentionProperty));
        }
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CancelCopyInteraction();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnHolding(object? sender, HoldingRoutedEventArgs e)
    {
        if (e.HoldingState == HoldingState.Started && isCopyHoldEligible)
        {
            suppressClick = true;
            e.Handled = true;
            CopyRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void CancelCopyInteraction()
    {
        isCopyHoldEligible = false;
        suppressClick |= copyPointer is not null;
        ReleaseCopyPointer();
    }

    private void ReleaseCopyPointer()
    {
        var pointer = copyPointer;
        copyPointer = null;
        if (ReferenceEquals(pointer?.Captured, this))
        {
            pointer!.Capture(null);
        }
    }

    private static PinyinLoadingPlaceholder CreatePinyinPlaceholder(string property, double length, TextBlock reference)
    {
        var placeholder = new PinyinLoadingPlaceholder { Length = length };
        placeholder.Bind(IsVisibleProperty, new Binding(property));
        placeholder.Bind(PinyinLoadingPlaceholder.FontSizeProperty, new DynamicResourceExtension("MessageFontSizePinyin"));
        placeholder.Bind(PinyinLoadingPlaceholder.ForegroundProperty, new DynamicResourceExtension("MessagePinyinBrush"));
        placeholder.Bind(PinyinLoadingPlaceholder.FontFamilyProperty, new Binding(nameof(TextBlock.FontFamily)) { Source = reference });
        placeholder.Bind(PinyinLoadingPlaceholder.FontStyleProperty, new Binding(nameof(TextBlock.FontStyle)) { Source = reference });
        placeholder.Bind(PinyinLoadingPlaceholder.FontStretchProperty, new Binding(nameof(TextBlock.FontStretch)) { Source = reference });
        placeholder.Bind(TextBlock.LineHeightProperty, new Binding(nameof(TextBlock.LineHeight)) { Source = reference });
        placeholder.Bind(TextBlock.LineSpacingProperty, new Binding(nameof(TextBlock.LineSpacing)) { Source = reference });
        return placeholder;
    }

    private static TextBlock BoundText(string property)
    {
        var text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        text.Bind(TextBlock.TextProperty, new Binding(property));
        return text;
    }
}
