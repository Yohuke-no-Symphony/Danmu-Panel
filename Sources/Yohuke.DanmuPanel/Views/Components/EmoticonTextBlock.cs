using Yohuke.DanmuPanel.I18n;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Yohuke.DanmuPanel.Models.Workspace;
using Yohuke.DanmuPanel.Models.Translation;

namespace Yohuke.DanmuPanel.Views.Components;

/// <summary>
/// 在弹幕原文中内联显示本地缓存的表情图片。
/// </summary>
public sealed class EmoticonTextBlock : TextBlock
{
    private const double EMOTICON_HEIGHT = 20;
    private readonly Dictionary<string, Bitmap> images = new(StringComparer.Ordinal);
    private readonly List<PinyinRubySyllable> rubySyllables = [];
    private readonly List<RenderedMessageTextSpan> renderedSpans = [];
    private readonly Dictionary<int, Run> pronunciationRuns = [];
    private PinyinSeparateLinePresenter? separateLine;
    private bool isAttached;

    internal int HighlightedSourceStart { get; private set; } = -1;
    internal bool HasAnnotations => rubySyllables.Count > 0 || pronunciationRuns.Count > 0;
    internal event Action? AnnotationLayoutChanged;

    /// <summary>
    /// 包含原始表情文本的弹幕正文。
    /// </summary>
    public static readonly StyledProperty<string?> ContentTextProperty =
        AvaloniaProperty.Register<EmoticonTextBlock, string?>(nameof(ContentText));

    /// <summary>
    /// 正文中的表情图片映射。
    /// </summary>
    public static readonly StyledProperty<IReadOnlyList<DanmuEmoticon>?> EmoticonsProperty =
        AvaloniaProperty.Register<EmoticonTextBlock, IReadOnlyList<DanmuEmoticon>?>(nameof(Emoticons));

    /// <summary>保留原文位置的正文注音片段。</summary>
    public static readonly StyledProperty<IReadOnlyList<PinyinAnnotation>?> AnnotationsProperty =
        AvaloniaProperty.Register<EmoticonTextBlock, IReadOnlyList<PinyinAnnotation>?>(nameof(Annotations));

    /// <summary>正文拼音的排版方式。</summary>
    public static readonly StyledProperty<PinyinLayoutMode> PinyinLayoutProperty =
        AvaloniaProperty.Register<EmoticonTextBlock, PinyinLayoutMode>(nameof(PinyinLayout));

    /// <summary>拼音使用的绝对字号。</summary>
    public static readonly StyledProperty<double> PinyinFontSizeProperty =
        AvaloniaProperty.Register<EmoticonTextBlock, double>(nameof(PinyinFontSize), 10);

    /// <summary>普通拼音前景色；未设置时跟随正文。</summary>
    public static readonly StyledProperty<IBrush?> PinyinForegroundProperty =
        AvaloniaProperty.Register<EmoticonTextBlock, IBrush?>(nameof(PinyinForeground));

    /// <summary>悬停音节前景色；未设置时使用主题强调色。</summary>
    public static readonly StyledProperty<IBrush?> PinyinHighlightForegroundProperty =
        AvaloniaProperty.Register<EmoticonTextBlock, IBrush?>(nameof(PinyinHighlightForeground));

    /// <summary>悬停音节背景色；未设置时使用主题选中背景。</summary>
    public static readonly StyledProperty<IBrush?> PinyinHighlightBackgroundProperty =
        AvaloniaProperty.Register<EmoticonTextBlock, IBrush?>(nameof(PinyinHighlightBackground));

    /// <summary>
    /// 包含原始表情文本的弹幕正文。
    /// </summary>
    public string? ContentText
    {
        get => GetValue(ContentTextProperty);
        set => SetValue(ContentTextProperty, value);
    }

    /// <summary>
    /// 正文中的表情图片映射。
    /// </summary>
    public IReadOnlyList<DanmuEmoticon>? Emoticons
    {
        get => GetValue(EmoticonsProperty);
        set => SetValue(EmoticonsProperty, value);
    }

    /// <summary>保留原文位置的正文注音片段。</summary>
    public IReadOnlyList<PinyinAnnotation>? Annotations
    {
        get => GetValue(AnnotationsProperty);
        set => SetValue(AnnotationsProperty, value);
    }

    /// <summary>正文拼音的排版方式。</summary>
    public PinyinLayoutMode PinyinLayout
    {
        get => GetValue(PinyinLayoutProperty);
        set => SetValue(PinyinLayoutProperty, value);
    }

    /// <summary>拼音使用的绝对字号。</summary>
    public double PinyinFontSize
    {
        get => GetValue(PinyinFontSizeProperty);
        set => SetValue(PinyinFontSizeProperty, value);
    }

    /// <summary>普通拼音前景色；未设置时跟随正文。</summary>
    public IBrush? PinyinForeground
    {
        get => GetValue(PinyinForegroundProperty);
        set => SetValue(PinyinForegroundProperty, value);
    }

    /// <summary>悬停音节前景色；未设置时使用主题强调色。</summary>
    public IBrush? PinyinHighlightForeground
    {
        get => GetValue(PinyinHighlightForegroundProperty);
        set => SetValue(PinyinHighlightForegroundProperty, value);
    }

    /// <summary>悬停音节背景色；未设置时使用主题选中背景。</summary>
    public IBrush? PinyinHighlightBackground
    {
        get => GetValue(PinyinHighlightBackgroundProperty);
        set => SetValue(PinyinHighlightBackgroundProperty, value);
    }

    /// <inheritdoc/>
    protected override Type StyleKeyOverride => typeof(TextBlock);

    /// <summary>修正 Avalonia 11.3.12 对 EmbeddedControlRun 使用严格大于等于比较而误判等宽溢出的边界。</summary>
    /// <remarks>只将原生排版约束增加一个浮点表示值，使恰好等宽的内联对象可放入该行，保留控件真实尺寸及单次测量。</remarks>
    protected override TextLayout CreateTextLayout(string? text)
    {
        if (!HasAnnotations || TextWrapping != TextWrapping.Wrap ||
            !double.IsFinite(_constraint.Width) || _constraint.Width <= 0)
        {
            return base.CreateTextLayout(text);
        }
        var constraint = _constraint;
        _constraint = new(Math.BitIncrement(constraint.Width), constraint.Height);
        try
        {
            return base.CreateTextLayout(text);
        }
        finally
        {
            _constraint = constraint;
        }
    }

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ContentTextProperty || change.Property == EmoticonsProperty ||
            change.Property == AnnotationsProperty || change.Property == PinyinLayoutProperty)
        {
            RebuildContent();
        }
        else if (change.Property == FontFamilyProperty || change.Property == FontSizeProperty ||
                 change.Property == FontWeightProperty || change.Property == FontStyleProperty ||
                 change.Property == FontStretchProperty || change.Property == ForegroundProperty ||
                 change.Property == TextWrappingProperty || change.Property == TextAlignmentProperty ||
                 change.Property == TextTrimmingProperty || change.Property == LineSpacingProperty ||
                 change.Property == LineHeightProperty || change.Property == PinyinFontSizeProperty ||
                 change.Property == PinyinForegroundProperty || change.Property == PinyinHighlightForegroundProperty ||
                 change.Property == PinyinHighlightBackgroundProperty ||
                 change.Property == ThemeVariantScope.ActualThemeVariantProperty)
        {
            if (change.Property == FontFamilyProperty || change.Property == FontSizeProperty ||
                change.Property == FontWeightProperty || change.Property == FontStyleProperty ||
                change.Property == FontStretchProperty || change.Property == PinyinFontSizeProperty ||
                change.Property == TextWrappingProperty || change.Property == TextAlignmentProperty ||
                change.Property == TextTrimmingProperty || change.Property == LineSpacingProperty ||
                change.Property == LineHeightProperty)
            {
                HighlightedSourceStart = -1;
            }
            UpdateTypography();
        }
        else if (change.Property == DataContextProperty || change.Property == IsVisibleProperty && !IsVisible)
        {
            ClearHighlight();
        }
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        UpdateHighlight(e.GetPosition(this));
    }

    /// <inheritdoc />
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        ClearHighlight();
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        isAttached = true;
        RebuildContent();
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        isAttached = false;
        ReleaseContent();
    }

    internal void UpdateHighlight(Point point)
    {
        var sourceStart = -1;
        foreach (var syllable in rubySyllables)
        {
            if (this.TranslatePoint(point, syllable.OriginalText) is { } local &&
                new Rect(syllable.OriginalText.Bounds.Size).Contains(local))
            {
                sourceStart = syllable.Annotation.SourceStart;
                break;
            }
        }

        if (separateLine is not null &&
            this.TranslatePoint(point, separateLine.OriginalText) is { } originalPoint)
        {
            var original = separateLine.OriginalText;
            var hit = original.TextLayout.HitTestPoint(originalPoint);
            var textIndex = hit.CharacterHit.FirstCharacterIndex;
            if (original.TextLayout.HitTestTextRange(textIndex, 1).Any(rectangle => rectangle.Contains(originalPoint)))
            {
                var span = renderedSpans.FirstOrDefault(value => textIndex >= value.TextStart &&
                                                                textIndex < value.TextStart + value.TextLength);
                if (span is { IsEmoticon: false })
                {
                    var rawIndex = span.SourceStart + textIndex - span.TextStart;
                    var annotation = Annotations!.FirstOrDefault(value => value.HasPinyin &&
                        rawIndex >= value.SourceStart && rawIndex < value.SourceStart + value.SourceLength);
                    sourceStart = annotation?.SourceStart ?? -1;
                }
            }
        }

        SetHighlightedSource(sourceStart);
    }

    internal void ClearHighlight()
    {
        SetHighlightedSource(-1);
    }

    private void RebuildContent()
    {
        ReleaseContent();
        var content = ContentText ?? "";
        var candidates = Emoticons?.Where(emoticon => !string.IsNullOrEmpty(emoticon.Text) &&
                                                     !string.IsNullOrWhiteSpace(emoticon.ImagePath))
            .OrderByDescending(emoticon => emoticon.Text.Length).ToArray() ?? [];

        var hasAnnotations = Annotations is { Count: > 0 } &&
                             Annotations.Any(annotation => annotation.HasPinyin) &&
                             string.Concat(Annotations.Select(annotation => annotation.Text)) == content;
        if (!isAttached || candidates.Length == 0 && !hasAnnotations)
        {
            Inlines = null;
            Text = content;
            AnnotationLayoutChanged?.Invoke();
            return;
        }

        Text = null;
        Inlines = new();
        var segments = ParseSegments(content, candidates);
        hasAnnotations = hasAnnotations && segments.Any(segment => segment.Emoticon is null &&
            GetAnnotations(segment).Any(annotation => annotation.HasPinyin));
        if (hasAnnotations && PinyinLayout == PinyinLayoutMode.SeparateLine)
        {
            separateLine = new(CreateOriginalInlines(segments), CreatePronunciationInlines(segments));
            Inlines.Add(new InlineUIContainer(separateLine));
        }
        else if (hasAnnotations)
        {
            foreach (var segment in segments)
            {
                if (segment.Emoticon is not null)
                {
                    Inlines.Add(CreateEmoticonInline(segment, true));
                    continue;
                }
                foreach (var annotation in GetAnnotations(segment))
                {
                    if (!annotation.HasPinyin)
                    {
                        Inlines.Add(new Run(annotation.Text));
                        continue;
                    }
                    var syllable = new PinyinRubySyllable(annotation);
                    rubySyllables.Add(syllable);
                    Inlines.Add(new InlineUIContainer(syllable));
                }
            }
        }
        else
        {
            Inlines = CreateOriginalInlines(segments);
        }
        UpdateTypography();
        AnnotationLayoutChanged?.Invoke();
    }

    private static IReadOnlyList<EmoticonTextSegment> ParseSegments(string content, IReadOnlyList<DanmuEmoticon> candidates)
    {
        var result = new List<EmoticonTextSegment>();
        var position = 0;

        while (position < content.Length)
        {
            DanmuEmoticon? match = null;
            var matchIndex = int.MaxValue;

            foreach (var candidate in candidates)
            {
                var index = content.IndexOf(candidate.Text, position, StringComparison.Ordinal);

                if (index >= 0 && index < matchIndex)
                {
                    match = candidate;
                    matchIndex = index;
                }
            }

            if (match is null)
            {
                result.Add(new(position, content[position..], null));
                break;
            }

            if (matchIndex > position)
            {
                result.Add(new(position, content[position..matchIndex], null));
            }
            result.Add(new(matchIndex, match.Text, match));
            position = matchIndex + match.Text.Length;
        }
        return result;
    }

    private InlineCollection CreateOriginalInlines(IReadOnlyList<EmoticonTextSegment> segments)
    {
        var result = new InlineCollection();
        var textIndex = 0;
        foreach (var segment in segments)
        {
            var inline = segment.Emoticon is null ? new Run(segment.Text) : CreateEmoticonInline(segment);
            result.Add(inline);
            var textLength = inline is InlineUIContainer ? 1 : segment.Text.Length;
            renderedSpans.Add(new(segment.SourceStart, textIndex, textLength, segment.Emoticon is not null));
            textIndex += textLength;
        }
        return result;
    }

    private Inline CreateEmoticonInline(EmoticonTextSegment segment, bool alignToTextBaseline = false)
    {
        var bitmap = GetImage(segment.Emoticon!.ImagePath);
        if (bitmap is null)
        {
            return new Run(segment.Text);
        }
        var image = new Image
        {
            Source = bitmap,
            Height = EMOTICON_HEIGHT,
            Width = EMOTICON_HEIGHT * bitmap.PixelSize.Width / bitmap.PixelSize.Height,
            Stretch = Stretch.Uniform
        };
        AutomationProperties.SetName(image, segment.Text);
        return new InlineUIContainer(image)
        {
            BaselineAlignment = alignToTextBaseline ? BaselineAlignment.Baseline : BaselineAlignment.Center
        };
    }

    private IEnumerable<PinyinAnnotation> GetAnnotations(EmoticonTextSegment segment)
    {
        var end = segment.SourceStart + segment.Text.Length;
        foreach (var annotation in Annotations!)
        {
            var start = Math.Max(segment.SourceStart, annotation.SourceStart);
            var length = Math.Min(end, annotation.SourceStart + annotation.SourceLength) - start;
            if (length > 0)
            {
                yield return start == annotation.SourceStart && length == annotation.SourceLength
                    ? annotation : new(start, annotation.Text.Substring(start - annotation.SourceStart, length), "");
            }
        }
    }

    private InlineCollection CreatePronunciationInlines(IReadOnlyList<EmoticonTextSegment> segments)
    {
        var result = new InlineCollection();
        var previousHadPinyin = false;
        var previousWasWhitespace = true;
        foreach (var segment in segments)
        {
            if (segment.Emoticon is not null)
            {
                result.Add(new Run(" "));
                previousHadPinyin = false;
                previousWasWhitespace = true;
                continue;
            }
            foreach (var annotation in GetAnnotations(segment))
            {
                var text = annotation.HasPinyin ? annotation.Pinyin : annotation.Text;
                if ((annotation.HasPinyin || previousHadPinyin) && !previousWasWhitespace &&
                    !char.IsWhiteSpace(text[0]))
                {
                    result.Add(new Run(" "));
                }
                var run = new Run(text);
                result.Add(run);
                if (annotation.HasPinyin)
                {
                    pronunciationRuns.Add(annotation.SourceStart, run);
                }
                previousHadPinyin = annotation.HasPinyin;
                previousWasWhitespace = char.IsWhiteSpace(text[^1]);
            }
        }
        return result;
    }

    private void UpdateTypography()
    {
        var foreground = PinyinForeground ?? Foreground;
        foreach (var syllable in rubySyllables)
        {
            syllable.UpdateTypography(this, foreground);
        }
        separateLine?.UpdateTypography(this, foreground);
        UpdateHighlightAppearance();
    }

    private void SetHighlightedSource(int sourceStart)
    {
        if (HighlightedSourceStart == sourceStart)
        {
            return;
        }
        HighlightedSourceStart = sourceStart;
        UpdateHighlightAppearance();
    }

    private void UpdateHighlightAppearance()
    {
        if (!HasAnnotations)
        {
            return;
        }
        var foreground = PinyinForeground ?? Foreground;
        var theme = GetValue(ThemeVariantScope.ActualThemeVariantProperty);
        var highlightForeground = PinyinHighlightForeground ??
                                  this.FindResource(theme, "AppPrimaryBrush") as IBrush ?? Foreground;
        var highlightBackground = PinyinHighlightBackground ??
                                  this.FindResource(theme, "AppSidebarSelectedBrush") as IBrush;
        foreach (var syllable in rubySyllables)
        {
            var highlighted = syllable.Annotation.SourceStart == HighlightedSourceStart;
            syllable.PronunciationText.Foreground = highlighted ? highlightForeground : foreground;
            syllable.PronunciationText.Background = highlighted ? highlightBackground : null;
        }
        foreach (var (sourceStart, run) in pronunciationRuns)
        {
            var highlighted = sourceStart == HighlightedSourceStart;
            run.Foreground = highlighted ? highlightForeground : foreground;
            run.Background = highlighted ? highlightBackground : null;
        }
    }

    private Bitmap? GetImage(string path)
    {
        if (images.TryGetValue(path, out var image))
        {
            return image;
        }

        try
        {
            using var stream = File.OpenRead(path);
            image = Bitmap.DecodeToHeight(stream, (int)(EMOTICON_HEIGHT * 2), BitmapInterpolationMode.HighQuality);
            images.Add(path, image);
            return image;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           ArgumentException or NotSupportedException or NullReferenceException)
        {
            System.Diagnostics.Trace.TraceWarning(Localization.Format("UI.EmoticonTextBlock.ImageDecodeFailed", exception.GetType().Name));
            return null;
        }
    }

    private void ReleaseContent()
    {
        HighlightedSourceStart = -1;
        rubySyllables.Clear();
        renderedSpans.Clear();
        pronunciationRuns.Clear();
        separateLine = null;
        Inlines?.Clear();

        foreach (var image in images.Values)
        {
            image.Dispose();
        }

        images.Clear();
    }
}
