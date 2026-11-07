using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.Models.Workspace;
using Yohuke.DanmuPanel.Models.Translation;

namespace Yohuke.DanmuPanel.Views.Components;

/// <summary>
/// 在弹幕原文中内联显示本地缓存的表情图片。
/// </summary>
public sealed class EmoticonTextBlock : TextBlock
{
    internal const double DEFAULT_EMOTICON_HEIGHT = 28;
    private const int MAX_CACHED_RUBY_INLINES = 64;
    private readonly Dictionary<string, EmoticonBitmapLease> images = new(StringComparer.Ordinal);
    private EmoticonBitmapCache? localBitmapCache;
    private EmoticonBitmapCache? bitmapCache;
    private readonly Dictionary<Image, DanmuEmoticon> emoticonImages = [];
    private EmoticonImagePreview? imagePreview;
    private readonly List<PinyinRubySyllable> rubySyllables = [];
    private readonly Queue<InlineUIContainer> rubyInlineCache = [];
    private readonly List<TextBlock> indivisibleTextElements = [];
    private readonly List<RenderedMessageTextSpan> renderedSpans = [];
    private readonly Dictionary<int, Run> pronunciationRuns = [];
    private List<Visual>? visibilityAncestors;
    private PinyinSeparateLinePresenter? separateLine;
    private bool isAttached;
    private bool isContentDeferred;
    private bool isVisibilityTracking;
    private bool isDataContextUpdating;
    private bool isContentUpdatePending;
    private CancellationTokenSource? imageRequest;
    private int contentRevision;

    internal Task PendingImageLoad { get; private set; } = Task.CompletedTask;
    internal Task PendingPreviewLoad => imagePreview?.PendingLoad ?? Task.CompletedTask;
    internal static readonly RoutedEvent<RoutedEventArgs> AsyncContentUpdatingEvent =
        RoutedEvent.Register<EmoticonTextBlock, RoutedEventArgs>("AsyncContentUpdating", RoutingStrategies.Bubble);
    internal static readonly RoutedEvent<RoutedEventArgs> AsyncContentUpdatedEvent =
        RoutedEvent.Register<EmoticonTextBlock, RoutedEventArgs>("AsyncContentUpdated", RoutingStrategies.Bubble);

    internal int HighlightedSourceStart { get; private set; } = -1;
    internal bool HasAnnotations => rubySyllables.Count > 0 || pronunciationRuns.Count > 0;
    internal bool IsSingleTextLine => HasSingleTextLine(this) && (separateLine is null ||
        HasSingleTextLine(separateLine.OriginalText) && HasSingleTextLine(separateLine.PronunciationText));
    internal event Action? AnnotationLayoutChanged;
    internal bool HasHoveredEmoticon => imagePreview?.HasTarget == true;
    internal int CachedRubyInlineCount => rubyInlineCache.Count;
    internal event Action? EmoticonHoverChanged;

    internal EmoticonBitmapCache? BitmapCache
    {
        get => bitmapCache;
        set
        {
            if (ReferenceEquals(bitmapCache, value))
            {
                return;
            }
            if (isAttached && bitmapCache is not null)
            {
                bitmapCache.ImageChanged -= OnImageChanged;
            }
            bitmapCache = value;
            if (isAttached && bitmapCache is not null)
            {
                bitmapCache.ImageChanged += OnImageChanged;
            }
            RebuildContent();
        }
    }

    /// <summary>内联表情的显示高度，独立于宽松和紧凑文字字号，并继承消息缩放。</summary>
    public static readonly AttachedProperty<double> EmoticonHeightProperty =
        AvaloniaProperty.RegisterAttached<EmoticonTextBlock, Control, double>(nameof(EmoticonHeight),
            DEFAULT_EMOTICON_HEIGHT, inherits: true, validate: value => double.IsFinite(value) && value > 0);

    /// <summary>内联表情的显示高度。</summary>
    public double EmoticonHeight
    {
        get => GetValue(EmoticonHeightProperty);
        set => SetValue(EmoticonHeightProperty, value);
    }

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

    internal static bool HasSingleTextLine(TextBlock text)
    {
        var lines = text.TextLayout.TextLines;
        return lines.Count <= 1 && (lines.Count == 0 || !lines[0].HasCollapsed);
    }

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
            change.Property == AnnotationsProperty || change.Property == PinyinLayoutProperty ||
            change.Property == EmoticonHeightProperty)
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
        else if (change.Property == IsVisibleProperty)
        {
            UpdateContentVisibility();
        }
        else if (change.Property == DataContextProperty)
        {
            ClearHighlight();
            ClearEmoticonHover();
        }
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        UpdateHighlight(e.GetPosition(this));
        UpdateEmoticonHover(e.GetPosition(this));
    }

    /// <inheritdoc />
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        ClearHighlight();
        ClearEmoticonHover();
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        ClearEmoticonHover();
        base.OnPointerPressed(e);
    }

    /// <inheritdoc />
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        ClearEmoticonHover();
        base.OnPointerWheelChanged(e);
    }

    internal void UpdateEmoticonHover(Point point, Control? tooltipHost = null)
    {
        foreach (var (image, emoticon) in emoticonImages)
        {
            if (image.IsEffectivelyVisible && this.TranslatePoint(point, image) is { } local &&
                new Rect(image.Bounds.Size).Contains(local))
            {
                if (imagePreview is null)
                {
                    imagePreview = new();
                    imagePreview.TargetChanged += () => EmoticonHoverChanged?.Invoke();
                }
                imagePreview.UpdateTarget(image, emoticon, tooltipHost ?? this);
                return;
            }
        }
        ClearEmoticonHover();
    }

    internal void ClearEmoticonHover()
    {
        imagePreview?.Clear();
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        isAttached = true;
        if (BitmapCache is not null)
        {
            BitmapCache.ImageChanged += OnImageChanged;
        }
        RebuildContent();
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        isAttached = false;
        if (BitmapCache is not null)
        {
            BitmapCache.ImageChanged -= OnImageChanged;
        }
        UpdateVisibilityTracking(false);
        ReleaseContent();
        localBitmapCache?.Clear();
    }

    /// <inheritdoc />
    protected override void OnDataContextBeginUpdate()
    {
        isDataContextUpdating = true;
        base.OnDataContextBeginUpdate();
    }

    /// <inheritdoc />
    protected override void OnDataContextEndUpdate()
    {
        base.OnDataContextEndUpdate();
        isDataContextUpdating = false;
        if (isContentUpdatePending)
        {
            RebuildContent();
        }
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

    private void OnAncestorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == IsVisibleProperty)
        {
            UpdateContentVisibility();
        }
    }

    private void UpdateContentVisibility()
    {
        if (isVisibilityTracking && isContentDeferred != !IsEffectivelyVisible)
        {
            RebuildContent();
        }
    }

    private void UpdateVisibilityTracking(bool required)
    {
        if (isVisibilityTracking == required)
        {
            return;
        }
        isVisibilityTracking = required;
        if (required)
        {
            visibilityAncestors ??= [];
            foreach (var ancestor in this.GetVisualAncestors())
            {
                visibilityAncestors.Add(ancestor);
                ancestor.PropertyChanged += OnAncestorPropertyChanged;
            }
        }
        else
        {
            foreach (var ancestor in visibilityAncestors!)
            {
                ancestor.PropertyChanged -= OnAncestorPropertyChanged;
            }
            visibilityAncestors.Clear();
        }
    }

    private void RebuildContent()
    {
        if (isDataContextUpdating)
        {
            isContentUpdatePending = true;
            return;
        }
        isContentUpdatePending = false;
        ReleaseContent();
        var content = ContentText ?? "";
        var hasRichContent = Annotations?.Any(annotation => annotation.HasPinyin) == true ||
                             Emoticons?.Any(emoticon => !string.IsNullOrEmpty(emoticon.Text) &&
                                 !string.IsNullOrWhiteSpace(emoticon.ImagePath)) == true;
        UpdateVisibilityTracking(isAttached && hasRichContent);
        isContentDeferred = !isAttached || !IsEffectivelyVisible;
        if (isContentDeferred)
        {
            Inlines = null;
            Text = content;
            AnnotationLayoutChanged?.Invoke();
            return;
        }
        var candidates = Emoticons?.Where(emoticon => !string.IsNullOrEmpty(emoticon.Text) &&
                                                     !string.IsNullOrWhiteSpace(emoticon.ImagePath))
            .OrderByDescending(emoticon => emoticon.Text.Length).ToArray() ?? [];

        if (candidates.Length > 0)
        {
            var cache = BitmapCache ?? (localBitmapCache ??= new(0, 0));
            var height = (int)Math.Ceiling(EmoticonHeight * (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1));
            var missing = new List<string>();
            foreach (var path in candidates.Where(candidate => content.Contains(candidate.Text, StringComparison.Ordinal))
                         .Select(candidate => candidate.ImagePath).Distinct(StringComparer.Ordinal))
            {
                var lease = cache.TryAcquire(path, height);
                if (lease is not null)
                {
                    images.Add(path, lease);
                }
                else
                {
                    missing.Add(path);
                }
            }
            if (missing.Count > 0)
            {
                imageRequest = new();
                PendingImageLoad = LoadImagesAsync(cache, missing, height, candidates, contentRevision, imageRequest.Token);
            }
        }
        RenderContent(content, candidates);
    }

    private void RenderContent(string content, IReadOnlyList<DanmuEmoticon> candidates)
    {
        var hasAnnotations = Annotations is { Count: > 0 } &&
                             Annotations.Any(annotation => annotation.HasPinyin) &&
                             string.Concat(Annotations.Select(annotation => annotation.Text)) == content;
        if (candidates.Count == 0 && !hasAnnotations)
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
                        Inlines.Add(CreateUnannotatedInline(annotation.Text));
                        continue;
                    }
                    var inline = rubyInlineCache.TryDequeue(out var cached)
                        ? cached : new InlineUIContainer(new PinyinRubySyllable(annotation));
                    var syllable = (PinyinRubySyllable)inline.Child;
                    syllable.UpdateAnnotation(annotation);
                    rubySyllables.Add(syllable);
                    Inlines.Add(inline);
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

    private async Task LoadImagesAsync(EmoticonBitmapCache cache, IReadOnlyList<string> paths, int height,
        IReadOnlyList<DanmuEmoticon> candidates, int revision, CancellationToken cancellationToken)
    {
        var tasks = paths.Select(path => cache.AcquireAsync(path, height, cancellationToken)).ToArray();
        var accepted = false;
        try
        {
            await Task.Yield();
            var leases = await Task.WhenAll(tasks);
            if (cancellationToken.IsCancellationRequested || revision != contentRevision || !isAttached || !IsEffectivelyVisible)
            {
                return;
            }
            for (var index = 0; index < leases.Length; index++)
            {
                if (leases[index] is { } lease)
                {
                    images.Add(paths[index], lease);
                }
            }
            accepted = true;
            UpdateContent(() =>
            {
                ReleaseInlines();
                RenderContent(ContentText ?? "", candidates);
            });
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (!accepted)
            {
                foreach (var task in tasks)
                {
                    if (task.IsCompletedSuccessfully)
                    {
                        task.Result?.Dispose();
                    }
                }
            }
        }
    }

    private void OnImageChanged(string path, int height)
    {
        var currentHeight = (int)Math.Ceiling(EmoticonHeight * (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1));
        if (isAttached && IsEffectivelyVisible && height == currentHeight && images.ContainsKey(path))
        {
            UpdateContent(RebuildContent);
        }
    }

    private void UpdateContent(Action update)
    {
        RaiseEvent(new RoutedEventArgs(AsyncContentUpdatingEvent));
        try
        {
            update();
        }
        finally
        {
            RaiseEvent(new RoutedEventArgs(AsyncContentUpdatedEvent));
        }
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

    private Inline CreateUnannotatedInline(string text)
    {
        if (text.Length <= 1 || char.IsControl(text[0]) ||
            Rune.GetRuneAt(text, 0).Utf16SequenceLength == text.Length ||
            StringInfo.GetNextTextElementLength(text) != text.Length)
        {
            return new Run(text);
        }
        var element = new TextBlock { Text = text, TextWrapping = TextWrapping.NoWrap };
        indivisibleTextElements.Add(element);
        return new InlineUIContainer(element) { BaselineAlignment = BaselineAlignment.Baseline };
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
            Height = EmoticonHeight,
            Width = EmoticonHeight * bitmap.PixelSize.Width / bitmap.PixelSize.Height,
            Stretch = Stretch.Uniform
        };
        AutomationProperties.SetName(image, segment.Text);
        emoticonImages.Add(image, segment.Emoticon);
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
        foreach (var element in indivisibleTextElements)
        {
            element.FontFamily = FontFamily;
            element.FontSize = FontSize;
            element.FontWeight = FontWeight;
            element.FontStyle = FontStyle;
            element.FontStretch = FontStretch;
            element.Foreground = Foreground;
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
        if (images.TryGetValue(path, out var existing))
        {
            return existing.Image;
        }
        return null;
    }

    private void ReleaseContent()
    {
        contentRevision++;
        imageRequest?.Cancel();
        imageRequest?.Dispose();
        imageRequest = null;
        PendingImageLoad = Task.CompletedTask;
        ReleaseInlines();
        foreach (var image in images.Values)
        {
            image.Dispose();
        }
        images.Clear();
    }

    private void ReleaseInlines()
    {
        ClearEmoticonHover();
        emoticonImages.Clear();
        HighlightedSourceStart = -1;
        rubySyllables.Clear();
        indivisibleTextElements.Clear();
        renderedSpans.Clear();
        pronunciationRuns.Clear();
        separateLine = null;
        if (Inlines is not null)
        {
            foreach (var inline in Inlines)
            {
                if (inline is InlineUIContainer { Child: PinyinRubySyllable } rubyInline &&
                    rubyInlineCache.Count < MAX_CACHED_RUBY_INLINES)
                {
                    rubyInlineCache.Enqueue(rubyInline);
                }
            }
        }
        Inlines?.Clear();

    }
}
