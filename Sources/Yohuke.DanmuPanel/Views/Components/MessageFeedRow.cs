using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.ViewModels;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed class MessageFeedRow : Button
{
    /// <inheritdoc/>
    protected override Type StyleKeyOverride => typeof(Button);

    internal MessageFeedRow(MessageKind kind)
    {
        var identity = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };
        var time = BoundText(nameof(MessageItemViewModel.Time));
        time.Classes.Add("danmuFeedMeta");
        DockPanel.SetDock(time, Avalonia.Controls.Dock.Right);
        identity.Children.Add(time);
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
            var medalSlot = new Grid { Children = { medalFrame, noMedalFrame } };
            medalSlot.Classes.Add("danmuMedalSlot");
            identity.Children.Add(medalSlot);
        }
        var name = new EmoticonTextBlock();
        name.Bind(EmoticonTextBlock.ContentTextProperty, new Binding(nameof(MessageItemViewModel.Name)));
        name.Bind(EmoticonTextBlock.AnnotationsProperty, new Binding(nameof(MessageItemViewModel.NameAnnotations)));
        name.Bind(EmoticonTextBlock.PinyinLayoutProperty, new Binding(nameof(MessageItemViewModel.PinyinLayout)));
        name.Bind(EmoticonTextBlock.PinyinForegroundProperty, new DynamicResourceExtension("MessagePinyinBrush"));
        name.Bind(EmoticonTextBlock.PinyinFontSizeProperty, new DynamicResourceExtension("MessageFontSizePinyin"));
        name.Classes.Add("danmuFeedUser");
        name.TextAlignment = TextAlignment.Center;
        name.Bind(ToolTip.TipProperty, new Binding(nameof(MessageItemViewModel.Name)));
        identity.Children.Add(name);
        identity.Classes.Add("danmuFeedIdentity");
        var original = new EmoticonTextBlock { TextWrapping = TextWrapping.Wrap };
        original.Classes.Add("danmuFeedContent");
        original.Classes.Add("regularFeedContent");
        original.Classes.Add("originalText");
        original.Bind(EmoticonTextBlock.ContentTextProperty, new Binding(nameof(MessageItemViewModel.Detail)));
        original.Bind(EmoticonTextBlock.EmoticonsProperty, new Binding(nameof(MessageItemViewModel.Emoticons)));
        original.Bind(EmoticonTextBlock.AnnotationsProperty, new Binding(nameof(MessageItemViewModel.ContentPinyin)));
        original.Bind(EmoticonTextBlock.PinyinLayoutProperty, new Binding(nameof(MessageItemViewModel.PinyinLayout)));
        original.Bind(EmoticonTextBlock.PinyinForegroundProperty, new DynamicResourceExtension("MessagePinyinBrush"));
        original.Bind(EmoticonTextBlock.PinyinFontSizeProperty, new DynamicResourceExtension("MessageFontSizePinyin"));
        var translation = BoundText(nameof(MessageItemViewModel.Translation));
        translation.Classes.Add("danmuFeedContent");
        translation.Classes.Add("regularFeedContent");
        translation.Classes.Add("translatedText");
        translation.TextWrapping = TextWrapping.Wrap;
        var originalMarquee = CreateMarquee(nameof(MessageItemViewModel.Detail));
        originalMarquee.Classes.Add("originalText");
        originalMarquee.Bind(HoverMarqueeTextBlock.EmoticonsProperty, new Binding(nameof(MessageItemViewModel.Emoticons)));
        originalMarquee.Bind(HoverMarqueeTextBlock.AnnotationsProperty, new Binding(nameof(MessageItemViewModel.ContentPinyin)));
        originalMarquee.Bind(HoverMarqueeTextBlock.PinyinLayoutProperty, new Binding(nameof(MessageItemViewModel.PinyinLayout)));
        originalMarquee.Bind(HoverMarqueeTextBlock.PinyinForegroundProperty, new DynamicResourceExtension("MessagePinyinBrush"));
        originalMarquee.Bind(HoverMarqueeTextBlock.PinyinFontSizeProperty, new DynamicResourceExtension("MessageFontSizePinyin"));
        var translationMarquee = CreateMarquee(nameof(MessageItemViewModel.Translation));
        translationMarquee.Classes.Add("translatedText");
        var translatedContent = new Grid { Children = { translation, translationMarquee } };
        translatedContent.Bind(IsVisibleProperty, new Binding(nameof(MessageItemViewModel.HasTranslation)));
        var pendingTranslation = new ShimmerTextBlock { HorizontalAlignment = HorizontalAlignment.Left };
        pendingTranslation.Classes.Add("translationPending");
        pendingTranslation.Bind(ShimmerTextBlock.TextProperty, Localization.Observe("Panel.Translating"));
        pendingTranslation.Bind(IsVisibleProperty, new Binding(nameof(MessageItemViewModel.IsTranslating)));
        var originalRegion = new Grid { Children = { original, originalMarquee } };
        originalRegion.Bind(IsVisibleProperty, new Binding(nameof(MessageItemViewModel.ShowOriginal)));
        var translationRegion = new Grid { Children = { translatedContent, pendingTranslation } };
        translationRegion.Classes.Add("translationLine");
        translationRegion.Bind(IsVisibleProperty, new Binding(nameof(MessageItemViewModel.ShowTranslation)));
        var price = BoundText(nameof(MessageItemViewModel.Price));
        price.Classes.Add("scFeedPrice");
        price.HorizontalAlignment = HorizontalAlignment.Right;
        var body = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children = { originalRegion, translationRegion }
        };
        body.Classes.Add("messageBody");
        var layout = new Grid();
        if (kind == MessageKind.SuperChat)
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
            layout.ColumnDefinitions = new(kind is MessageKind.Gift or MessageKind.Guard ? "3*,2*" : "2*,3*");
            layout.Children.Add(identity);
            Grid.SetColumn(body, 1);
            layout.Children.Add(body);
            layout.Classes.Add("danmuFeedLayout");
        }
        var isCard = kind is MessageKind.SuperChat or MessageKind.Gift or MessageKind.Guard;
        var frame = new Border
        {
            BorderThickness = new Thickness(isCard ? 2 : 0),
            Child = layout
        };
        if (kind == MessageKind.SuperChat)
        {
            frame.Classes.Add("scFeedCard");
        }
        if (isCard)
        {
            frame.Classes.Add("messageCard");
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

    private static HoverMarqueeTextBlock CreateMarquee(string property)
    {
        var text = new HoverMarqueeTextBlock();
        text.Classes.Add("compactFeedContent");
        text.Bind(HoverMarqueeTextBlock.TextProperty, new Binding(property));
        text.Bind(HoverMarqueeTextBlock.AutoScrollProperty, new Binding(nameof(MessageItemViewModel.AutoScrollLongMessages)));
        return text;
    }

    private static TextBlock BoundText(string property)
    {
        var text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        text.Bind(TextBlock.TextProperty, new Binding(property));
        return text;
    }
}
