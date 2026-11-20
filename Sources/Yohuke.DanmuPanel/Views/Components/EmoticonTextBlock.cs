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
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.ViewModels;

namespace Yohuke.DanmuPanel.Views.Components;

/// <summary>
/// 在消息正文中内联显示回复徽章、表情图片和拼音注音。
/// </summary>
public class EmoticonTextBlock : TextBlock
{
    internal const double DEFAULT_EMOTICON_HEIGHT = 28;
    private const int MAX_CACHED_RUBY_INLINES = 64;
    private readonly Dictionary<string, EmoticonBitmapLease> images = new(StringComparer.Ordinal);
    private EmoticonBitmapCache? localBitmapCache;
    private EmoticonBitmapCache? bitmapCache;
    private readonly Dictionary<string, List<EmoticonInlineSlot>> imageSlots = new(StringComparer.Ordinal);
    private readonly Dictionary<Image, DanmuEmoticon> emoticonImages = [];
    private EmoticonImagePreview? imagePreview;
    private string? hoveredEmoticonPath;
    private readonly List<PinyinRubySyllable> rubySyllables = [];
    private readonly Queue<InlineUIContainer> rubyInlineCache = [];
    private readonly List<TextBlock> indivisibleTextElements = [];
    private readonly List<RenderedMessageTextSpan> renderedSpans = [];
    private readonly Dictionary<int, Run> pronunciationRuns = [];
    private List<Visual>? visibilityAncestors;
    private PinyinSeparateLinePresenter? separateLine;
    private MessageReplyBadge? replyBadge;
    private MessageTextRenderPlan? cachedPlan;
    private MessageTextVisualState? renderedState;
    private bool isAttached;
    private bool isContentDeferred;
    private bool isVisibilityTracking;
    private bool isDataContextUpdating;
    private bool isContentUpdatePending;
    private bool isImageRefreshPending;
    private CancellationTokenSource? imageRequest;
    private int contentRevision;

    internal Task PendingImageLoad { get; private set; } = Task.CompletedTask;
    internal IUserAvatarService? AvatarService { get; set; }
    internal Task PendingPreviewLoad => imagePreview?.PendingLoad ?? Task.CompletedTask;
    internal static readonly RoutedEvent<RoutedEventArgs> AsyncContentUpdatingEvent =
        RoutedEvent.Register<EmoticonTextBlock, RoutedEventArgs>("AsyncContentUpdating", RoutingStrategies.Bubble);
    internal static readonly RoutedEvent<RoutedEventArgs> AsyncContentUpdatedEvent =
        RoutedEvent.Register<EmoticonTextBlock, RoutedEventArgs>("AsyncContentUpdated", RoutingStrategies.Bubble);

    internal int HighlightedSourceStart { get; private set; } = -1;
    internal bool HasAnnotations => rubySyllables.Count > 0 || pronunciationRuns.Count > 0;
    internal bool IsSingleTextLine => HasSingleTextLine(this) && (separateLine is null ||
        HasSingleTextLine(separateLine.OriginalText) && HasSingleTextLine(separateLine.PronunciationText));
    internal double InlineTextWidth => separateLine is null ? DesiredSize.Width :
        Padding.Left + separateLine.OriginalText.DesiredSize.Width + Padding.Right;
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
            RefreshImages();
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

    /// <summary>在正文前显示的回复对象。</summary>
    public static readonly StyledProperty<MessageReply?> ReplyProperty =
        AvaloniaProperty.Register<EmoticonTextBlock, MessageReply?>(nameof(Reply));

    /// <summary>是否显示回复徽章中的头像。</summary>
    public static readonly StyledProperty<bool> ShowReplyAvatarProperty =
        AvaloniaProperty.Register<EmoticonTextBlock, bool>(nameof(ShowReplyAvatar), true);

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

    /// <summary>在正文前显示的回复对象。</summary>
    public MessageReply? Reply
    {
        get => GetValue(ReplyProperty);
        set => SetValue(ReplyProperty, value);
    }

    /// <summary>是否显示回复徽章中的头像。</summary>
    public bool ShowReplyAvatar
    {
        get => GetValue(ShowReplyAvatarProperty);
        set => SetValue(ShowReplyAvatarProperty, value);
    }

    private bool HasReply => Reply is { } reply && !string.IsNullOrWhiteSpace(reply.UserName);

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

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        if (replyBadge is not null)
        {
            replyBadge.MaxWidth = Math.Max(0, availableSize.Width - Padding.Left - Padding.Right);
        }
        return base.MeasureOverride(availableSize);
    }

    /// <summary>修正 Avalonia 11.3.12 对 EmbeddedControlRun 使用严格大于等于比较而误判等宽溢出的边界。</summary>
    /// <remarks>只将原生排版约束增加一个浮点表示值，使恰好等宽的内联对象可放入该行，保留控件真实尺寸及单次测量。</remarks>
    protected override TextLayout CreateTextLayout(string? text)
    {
        if ((!HasAnnotations && replyBadge is null) || TextWrapping != TextWrapping.Wrap ||
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
            change.Property == ReplyProperty || change.Property == ShowReplyAvatarProperty ||
            change.Property == AnnotationsProperty || change.Property == PinyinLayoutProperty)
        {
            RebuildContent();
        }
        else if (change.Property == EmoticonHeightProperty)
        {
            RefreshImages();
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
            var metricsChanged = change.Property == FontFamilyProperty || change.Property == FontSizeProperty ||
                change.Property == FontWeightProperty || change.Property == FontStyleProperty ||
                change.Property == FontStretchProperty || change.Property == PinyinFontSizeProperty ||
                change.Property == TextWrappingProperty || change.Property == TextAlignmentProperty ||
                change.Property == TextTrimmingProperty || change.Property == LineSpacingProperty ||
                change.Property == LineHeightProperty;
            if (metricsChanged)
            {
                HighlightedSourceStart = -1;
            }
            UpdateTypography();
            if (metricsChanged)
            {
                AnnotationLayoutChanged?.Invoke();
            }
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
                hoveredEmoticonPath = emoticon.ImagePath;
                return;
            }
        }
        ClearEmoticonHover();
    }

    internal void ClearEmoticonHover()
    {
        hoveredEmoticonPath = null;
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
        if (isImageRefreshPending)
        {
            RefreshImages();
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
                if (span is { TextLength: > 0, IsEmoticon: false })
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
        var content = ContentText ?? "";
        var hasEmoticons = Emoticons is { Count: > 0 };
        var hasPinyin = Annotations is { Count: > 0 } annotations && annotations.Any(annotation => annotation.HasPinyin);
        var reply = HasReply ? Reply : null;
        isContentDeferred = !isAttached || !IsEffectivelyVisible;
        if (isContentDeferred)
        {
            UpdateVisibilityTracking(isAttached && (hasEmoticons || hasPinyin || reply is not null));
            if (cachedPlan?.Matches(content, Emoticons, Annotations) == false)
            {
                cachedPlan = null;
            }
            ShowPlainText(content);
            return;
        }

        var plan = hasEmoticons || hasPinyin ? ResolvePlan(content) : null;
        if (plan is { HasAnnotations: false, ImagePaths.Count: 0 })
        {
            plan = null;
        }

        UpdateVisibilityTracking(plan is not null || reply is not null);
        if (plan is null && reply is null)
        {
            if (!hasEmoticons && !hasPinyin)
            {
                cachedPlan = null;
            }
            ShowPlainText(content);
            return;
        }

        var state = new MessageTextVisualState(content, plan, reply, reply is not null && ShowReplyAvatar,
            plan?.HasAnnotations == true && PinyinLayout == PinyinLayoutMode.SeparateLine);
        if (renderedState == state)
        {
            return;
        }

        ReleaseContent();
        renderedState = state;
        if (plan is not { ImagePaths.Count: > 0 })
        {
            RenderContent(state);
            return;
        }

        var cache = BitmapCache ?? (localBitmapCache ??= new(0, 0));
        var height = GetDecodeHeight();
        var missing = new List<string>();
        foreach (var path in plan.ImagePaths)
        {
            if (cache.TryAcquire(path, height) is { } lease)
            {
                images.Add(path, lease);
            }
            else
            {
                missing.Add(path);
            }
        }

        var revision = contentRevision;
        RenderContent(state);
        if (revision == contentRevision)
        {
            StartImageLoad(cache, missing, height);
        }
    }

    private MessageTextRenderPlan ResolvePlan(string content)
    {
        if (cachedPlan is { } plan && plan.Matches(content, Emoticons, Annotations))
        {
            return plan;
        }

        cachedPlan = (DataContext as MessageItemViewModel)?.GetTextRenderPlan(content, Emoticons, Annotations) ??
            MessageTextRenderPlan.Create(content, Emoticons, Annotations);
        return cachedPlan;
    }

    private void ShowPlainText(string content)
    {
        if (Inlines is null && string.Equals(Text, content, StringComparison.Ordinal))
        {
            return;
        }

        if (renderedState is not null)
        {
            ReleaseContent();
        }
        Inlines = null;
        Text = content;
        AnnotationLayoutChanged?.Invoke();
    }

    private void RenderContent(MessageTextVisualState state)
    {
        Text = null;
        if (state.SeparateLine)
        {
            separateLine = new(CreateOriginalInlines(state), CreatePronunciationInlines(state.Plan!.Runs));
            Inlines = new() { new InlineUIContainer(separateLine) };
            UpdateRenderedSpans();
        }
        else if (state.Plan is { HasAnnotations: true } plan)
        {
            var inlines = new InlineCollection();
            AddReplyInline(inlines, state);
            foreach (var run in plan.Runs)
            {
                if (run.Emoticon is { } emoticon)
                {
                    AddEmoticonInline(inlines, emoticon, alignToTextBaseline: true);
                    continue;
                }
                foreach (var annotation in run.Annotations)
                {
                    if (!annotation.HasPinyin)
                    {
                        inlines.Add(CreateUnannotatedInline(annotation.Text));
                        continue;
                    }
                    var inline = rubyInlineCache.TryDequeue(out var cached)
                        ? cached : new InlineUIContainer(new PinyinRubySyllable(annotation));
                    var syllable = (PinyinRubySyllable)inline.Child;
                    syllable.UpdateAnnotation(annotation);
                    rubySyllables.Add(syllable);
                    inlines.Add(inline);
                }
            }
            Inlines = inlines;
        }
        else
        {
            Inlines = CreateOriginalInlines(state);
        }

        UpdateTypography();
        AnnotationLayoutChanged?.Invoke();
    }

    private void RefreshImages()
    {
        if (isDataContextUpdating)
        {
            isImageRefreshPending = true;
            return;
        }

        isImageRefreshPending = false;
        if (renderedState?.Plan is not { ImagePaths.Count: > 0 } plan || !isAttached || !IsEffectivelyVisible)
        {
            return;
        }

        CancelImageLoad();
        var cache = BitmapCache ?? (localBitmapCache ??= new(0, 0));
        var height = GetDecodeHeight();
        var paths = plan.ImagePaths;
        var leases = new EmoticonBitmapLease?[paths.Count];
        var missing = new List<string>();
        var revision = contentRevision;
        try
        {
            for (var index = 0; index < paths.Count; index++)
            {
                var path = paths[index];
                leases[index] = cache.TryAcquire(path, height);
                if (leases[index] is null)
                {
                    missing.Add(path);
                }
            }

            ApplyImageLeases(paths, leases, updateSize: true);
        }
        finally
        {
            DisposeLeases(leases);
        }
        if (revision == contentRevision)
        {
            StartImageLoad(cache, missing, height);
        }
    }

    private int GetDecodeHeight()
    {
        return (int)Math.Ceiling(EmoticonHeight * (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1));
    }

    private void StartImageLoad(EmoticonBitmapCache cache, IReadOnlyList<string> paths, int height)
    {
        if (paths.Count == 0)
        {
            return;
        }

        imageRequest ??= new();
        var load = LoadImagesAsync(cache, paths, height, contentRevision, imageRequest.Token);
        PendingImageLoad = PendingImageLoad.IsCompletedSuccessfully ? load : Task.WhenAll(PendingImageLoad, load);
    }

    private async Task LoadImagesAsync(EmoticonBitmapCache cache, IReadOnlyList<string> paths, int height, int revision,
        CancellationToken cancellationToken)
    {
        var tasks = paths.Select(path => cache.AcquireAsync(path, height, cancellationToken)).ToArray();
        EmoticonBitmapLease?[]? leases = null;
        try
        {
            await Task.Yield();
            leases = await Task.WhenAll(tasks);
            if (!cancellationToken.IsCancellationRequested && revision == contentRevision && isAttached && IsEffectivelyVisible)
            {
                ApplyImageLeases(paths, leases);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (leases is not null)
            {
                DisposeLeases(leases);
            }
            else
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
        if (!isAttached || !IsEffectivelyVisible || height != GetDecodeHeight() || !images.ContainsKey(path))
        {
            return;
        }

        var cache = BitmapCache ?? localBitmapCache!;
        if (cache.TryAcquire(path, height) is not { } lease)
        {
            StartImageLoad(cache, [path], height);
            return;
        }

        EmoticonBitmapLease?[] leases = [lease];
        try
        {
            ApplyImageLeases([path], leases);
        }
        finally
        {
            DisposeLeases(leases);
        }
    }

    private void ApplyImageLeases(IReadOnlyList<string> paths, EmoticonBitmapLease?[] leases, bool updateSize = false)
    {
        var changed = false;
        var revision = contentRevision;
        try
        {
            for (var index = 0; index < paths.Count; index++)
            {
                var path = paths[index];
                var replacement = leases[index];
                images.TryGetValue(path, out var previous);
                var bitmap = replacement?.Image;
                if (ReferenceEquals(previous?.Image, bitmap) && (!updateSize || bitmap is null))
                {
                    continue;
                }

                if (!changed)
                {
                    changed = true;
                    RaiseEvent(new RoutedEventArgs(AsyncContentUpdatingEvent));
                }
                if (previous is not null && string.Equals(path, hoveredEmoticonPath, StringComparison.Ordinal))
                {
                    ClearEmoticonHover();
                }
                if (contentRevision != revision)
                {
                    return;
                }

                UpdateEmoticonInlines(path, bitmap);
                if (contentRevision != revision)
                {
                    return;
                }
                if (replacement is null)
                {
                    images.Remove(path);
                }
                else
                {
                    images[path] = replacement;
                    leases[index] = null;
                }
                previous?.Dispose();
            }

            if (changed)
            {
                UpdateRenderedSpans();
                separateLine?.OriginalText.InvalidateMeasure();
                InvalidateTextLayout();
                AnnotationLayoutChanged?.Invoke();
            }
        }
        finally
        {
            if (changed)
            {
                RaiseEvent(new RoutedEventArgs(AsyncContentUpdatedEvent));
            }
        }
    }

    private static void DisposeLeases(IEnumerable<EmoticonBitmapLease?> leases)
    {
        foreach (var lease in leases)
        {
            lease?.Dispose();
        }
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

    private InlineCollection CreateOriginalInlines(MessageTextVisualState state)
    {
        var inlines = new InlineCollection();
        AddReplyInline(inlines, state);
        if (state.Plan is not { } plan)
        {
            if (state.Content.Length > 0)
            {
                inlines.Add(new Run(state.Content));
            }
            return inlines;
        }

        foreach (var run in plan.Runs)
        {
            if (run.Emoticon is { } emoticon)
            {
                AddEmoticonInline(inlines, emoticon);
            }
            else
            {
                inlines.Add(new Run(run.Text));
            }
        }
        return inlines;
    }

    private void AddReplyInline(InlineCollection inlines, MessageTextVisualState state)
    {
        if (state.Reply is not { } reply)
        {
            return;
        }

        replyBadge = new(reply, AvatarService, state.ShowReplyAvatar);
        inlines.Add(new InlineUIContainer(replyBadge) { BaselineAlignment = BaselineAlignment.Baseline });
        inlines.Add(new Run(" "));
    }

    private void AddEmoticonInline(InlineCollection inlines, DanmuEmoticon emoticon, bool alignToTextBaseline = false)
    {
        if (!imageSlots.TryGetValue(emoticon.ImagePath, out var slots))
        {
            slots = [];
            imageSlots.Add(emoticon.ImagePath, slots);
        }
        var slot = new EmoticonInlineSlot(inlines, inlines.Count, emoticon, alignToTextBaseline);
        slots.Add(slot);
        inlines.Add(CreateEmoticonInline(slot, GetImage(emoticon.ImagePath)));
    }

    private Inline CreateEmoticonInline(EmoticonInlineSlot slot, Bitmap? bitmap)
    {
        if (bitmap is null)
        {
            return new Run(slot.Emoticon.Text);
        }

        var image = new Image { Stretch = Stretch.Uniform };
        SetEmoticonBitmap(image, bitmap);
        AutomationProperties.SetName(image, slot.Emoticon.Text);
        emoticonImages.Add(image, slot.Emoticon);
        return new InlineUIContainer(image)
        {
            BaselineAlignment = slot.AlignToTextBaseline ? BaselineAlignment.Baseline : BaselineAlignment.Center
        };
    }

    private void UpdateEmoticonInlines(string path, Bitmap? bitmap)
    {
        foreach (var slot in imageSlots[path])
        {
            if (slot.Inlines[slot.Index] is InlineUIContainer { Child: Image image })
            {
                if (bitmap is not null)
                {
                    SetEmoticonBitmap(image, bitmap);
                    continue;
                }

                emoticonImages.Remove(image);
            }
            slot.Inlines[slot.Index] = CreateEmoticonInline(slot, bitmap);
        }
    }

    private void SetEmoticonBitmap(Image image, Bitmap bitmap)
    {
        image.Width = EmoticonHeight * bitmap.PixelSize.Width / bitmap.PixelSize.Height;
        image.Height = EmoticonHeight;
        image.Source = bitmap;
    }

    private void UpdateRenderedSpans()
    {
        if (separateLine?.OriginalText.Inlines is not { } inlines || renderedState?.Plan is not { } plan)
        {
            return;
        }

        renderedSpans.Clear();
        var prefixLength = replyBadge is null ? 0 : 2;
        var textIndex = prefixLength;
        for (var index = 0; index < plan.Runs.Count; index++)
        {
            var run = plan.Runs[index];
            var length = inlines[index + prefixLength] is InlineUIContainer ? 1 : run.Text.Length;
            renderedSpans.Add(new(run.SourceStart, textIndex, length, run.Emoticon is not null));
            textIndex += length;
        }
    }

    private InlineCollection CreatePronunciationInlines(IReadOnlyList<MessageTextRun> runs)
    {
        var result = new InlineCollection();
        var previousHadPinyin = false;
        var previousWasWhitespace = true;
        foreach (var run in runs)
        {
            if (run.Emoticon is not null)
            {
                result.Add(new Run(" "));
                previousHadPinyin = false;
                previousWasWhitespace = true;
                continue;
            }
            foreach (var annotation in run.Annotations)
            {
                var text = annotation.HasPinyin ? annotation.Pinyin : annotation.Text;
                if ((annotation.HasPinyin || previousHadPinyin) && !previousWasWhitespace &&
                    !char.IsWhiteSpace(text[0]))
                {
                    result.Add(new Run(" "));
                }
                var pronunciation = new Run(text);
                result.Add(pronunciation);
                if (annotation.HasPinyin)
                {
                    pronunciationRuns.Add(annotation.SourceStart, pronunciation);
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
        replyBadge?.UpdateTypography(this);
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
        CancelImageLoad();
        renderedState = null;
        isImageRefreshPending = false;
        ReleaseInlines();
        DisposeLeases(images.Values);
        images.Clear();
    }

    private void CancelImageLoad()
    {
        contentRevision++;
        imageRequest?.Cancel();
        imageRequest?.Dispose();
        imageRequest = null;
        PendingImageLoad = Task.CompletedTask;
    }

    private void ReleaseInlines()
    {
        ClearEmoticonHover();
        imageSlots.Clear();
        emoticonImages.Clear();
        HighlightedSourceStart = -1;
        rubySyllables.Clear();
        indivisibleTextElements.Clear();
        renderedSpans.Clear();
        pronunciationRuns.Clear();
        separateLine = null;
        replyBadge = null;
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
