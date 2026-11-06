using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Models.Workspace;

namespace Yohuke.DanmuPanel.Views.Components;

/// <summary>
/// 支持悬停滚动及自动循环滚动的单行文本控件。
/// </summary>
public sealed class HoverMarqueeTextBlock : UserControl
{
    private const double SCROLL_SPEED = 60;
    private static readonly TimeSpan START_DELAY = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan TIMER_INTERVAL = TimeSpan.FromMilliseconds(16);
    private readonly HoverMarqueeController controller = new(START_DELAY, SCROLL_SPEED);
    private readonly Stopwatch stopwatch = new();
    private readonly ScrollViewer scrollViewer;
    private readonly EmoticonTextBlock textBlock;
    private readonly List<Visual> visibilityAncestors = [];
    private bool initialized;
    private bool isAttached;
    private bool isInViewport;
    private readonly DispatcherTimer timer = new()
    {
        Interval = TIMER_INTERVAL
    };

    internal double TextBaseline => textBlock.Padding.Top + textBlock.TextLayout.Baseline;
    internal bool IsSingleTextLine => textBlock.IsSingleTextLine;

    /// <summary>
    /// 显示文本。
    /// </summary>
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<HoverMarqueeTextBlock, string?>(nameof(Text));

    /// <summary>
    /// 显示文本中的表情图片映射。
    /// </summary>
    public static readonly StyledProperty<IReadOnlyList<DanmuEmoticon>?> EmoticonsProperty =
        AvaloniaProperty.Register<HoverMarqueeTextBlock, IReadOnlyList<DanmuEmoticon>?>(nameof(Emoticons));

    /// <summary>是否在可见且内容超宽时自动循环滚动。</summary>
    public static readonly StyledProperty<bool> AutoScrollProperty =
        AvaloniaProperty.Register<HoverMarqueeTextBlock, bool>(nameof(AutoScroll));

    /// <summary>保留原文位置的正文注音片段。</summary>
    public static readonly StyledProperty<IReadOnlyList<PinyinAnnotation>?> AnnotationsProperty =
        EmoticonTextBlock.AnnotationsProperty.AddOwner<HoverMarqueeTextBlock>();

    /// <summary>正文拼音的排版方式。</summary>
    public static readonly StyledProperty<PinyinLayoutMode> PinyinLayoutProperty =
        EmoticonTextBlock.PinyinLayoutProperty.AddOwner<HoverMarqueeTextBlock>();

    /// <summary>拼音使用的绝对字号。</summary>
    public static readonly StyledProperty<double> PinyinFontSizeProperty =
        EmoticonTextBlock.PinyinFontSizeProperty.AddOwner<HoverMarqueeTextBlock>();

    /// <summary>普通拼音前景色。</summary>
    public static readonly StyledProperty<IBrush?> PinyinForegroundProperty =
        EmoticonTextBlock.PinyinForegroundProperty.AddOwner<HoverMarqueeTextBlock>();

    /// <summary>悬停音节前景色。</summary>
    public static readonly StyledProperty<IBrush?> PinyinHighlightForegroundProperty =
        EmoticonTextBlock.PinyinHighlightForegroundProperty.AddOwner<HoverMarqueeTextBlock>();

    /// <summary>悬停音节背景色。</summary>
    public static readonly StyledProperty<IBrush?> PinyinHighlightBackgroundProperty =
        EmoticonTextBlock.PinyinHighlightBackgroundProperty.AddOwner<HoverMarqueeTextBlock>();

    /// <summary>
    /// 初始化悬停滚动文本控件。
    /// </summary>
    public HoverMarqueeTextBlock()
    {
        textBlock = new()
        {
            TextWrapping = TextWrapping.NoWrap,
            TextAlignment = TextAlignment.Left,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        textBlock.Bind(TextBlock.FontSizeProperty, new Binding
        {
            Path = nameof(FontSize),
            Source = this
        });
        textBlock.Bind(TextBlock.ForegroundProperty, new Binding
        {
            Path = nameof(Foreground),
            Source = this
        });
        textBlock.Bind(EmoticonTextBlock.ContentTextProperty, new Binding
        {
            Path = nameof(Text),
            Source = this
        });
        textBlock.Bind(EmoticonTextBlock.EmoticonsProperty, new Binding
        {
            Path = nameof(Emoticons),
            Source = this
        });
        textBlock.Bind(EmoticonTextBlock.AnnotationsProperty, new Binding(nameof(Annotations)) { Source = this });
        textBlock.Bind(EmoticonTextBlock.PinyinLayoutProperty, new Binding(nameof(PinyinLayout)) { Source = this });
        textBlock.Bind(EmoticonTextBlock.PinyinFontSizeProperty, new Binding(nameof(PinyinFontSize)) { Source = this });
        textBlock.Bind(EmoticonTextBlock.PinyinForegroundProperty, new Binding(nameof(PinyinForeground)) { Source = this });
        textBlock.Bind(EmoticonTextBlock.PinyinHighlightForegroundProperty,
            new Binding(nameof(PinyinHighlightForeground)) { Source = this });
        textBlock.Bind(EmoticonTextBlock.PinyinHighlightBackgroundProperty,
            new Binding(nameof(PinyinHighlightBackground)) { Source = this });

        scrollViewer = new()
        {
            Content = textBlock,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };

        Content = scrollViewer;
        Background = Brushes.Transparent;
        ClipToBounds = true;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Center;

        timer.Tick += Timer_OnTick;
        PointerEntered += Control_OnPointerEntered;
        PointerExited += Control_OnPointerExited;
        PointerMoved += (_, args) => textBlock.UpdateHighlight(args.GetPosition(textBlock));
        textBlock.AnnotationLayoutChanged += () =>
        {
            if (initialized)
            {
                UpdateScrolling();
            }
        };
        AttachedToVisualTree += (_, _) =>
        {
            isAttached = true;
            foreach (var ancestor in this.GetVisualAncestors())
            {
                visibilityAncestors.Add(ancestor);
                ancestor.PropertyChanged += Ancestor_OnPropertyChanged;
            }
            UpdateScrolling();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            isAttached = false;
            isInViewport = false;
            foreach (var ancestor in visibilityAncestors)
            {
                ancestor.PropertyChanged -= Ancestor_OnPropertyChanged;
            }
            visibilityAncestors.Clear();
            StopScrolling();
        };
        EffectiveViewportChanged += (_, args) =>
        {
            isInViewport = args.EffectiveViewport.Intersects(new Rect(Bounds.Size));
            UpdateScrolling();
        };
        scrollViewer.PropertyChanged += ScrollViewer_OnPropertyChanged;
        initialized = true;
    }

    /// <summary>
    /// 显示文本。
    /// </summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>
    /// 显示文本中的表情图片映射。
    /// </summary>
    public IReadOnlyList<DanmuEmoticon>? Emoticons
    {
        get => GetValue(EmoticonsProperty);
        set => SetValue(EmoticonsProperty, value);
    }

    /// <summary>自动滚动可见的超长内容；关闭时仍支持悬停滚动。</summary>
    public bool AutoScroll
    {
        get => GetValue(AutoScrollProperty);
        set => SetValue(AutoScrollProperty, value);
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

    /// <summary>普通拼音前景色。</summary>
    public IBrush? PinyinForeground
    {
        get => GetValue(PinyinForegroundProperty);
        set => SetValue(PinyinForegroundProperty, value);
    }

    /// <summary>悬停音节前景色。</summary>
    public IBrush? PinyinHighlightForeground
    {
        get => GetValue(PinyinHighlightForegroundProperty);
        set => SetValue(PinyinHighlightForegroundProperty, value);
    }

    /// <summary>悬停音节背景色。</summary>
    public IBrush? PinyinHighlightBackground
    {
        get => GetValue(PinyinHighlightBackgroundProperty);
        set => SetValue(PinyinHighlightBackgroundProperty, value);
    }

    private bool IsAnimationAvailable => isAttached && isInViewport && IsEffectivelyVisible;
    private bool IsPausedForAnnotation => textBlock.HasAnnotations && IsPointerOver;
    private bool CanAnimate => IsAnimationAvailable && (AutoScroll || IsPointerOver) && !IsPausedForAnnotation;

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (!initialized)
        {
            return;
        }

        if (change.Property == TextProperty || change.Property == EmoticonsProperty ||
            change.Property == AutoScrollProperty || change.Property == DataContextProperty ||
            change.Property == AnnotationsProperty || change.Property == PinyinLayoutProperty)
        {
            StopScrolling();
            UpdateScrolling();
        }
        else if (change.Property == IsVisibleProperty)
        {
            UpdateScrolling();
        }
    }

    private void Control_OnPointerEntered(object? sender, PointerEventArgs e)
    {
        if (textBlock.HasAnnotations)
        {
            UpdateScrolling();
            return;
        }
        if (AutoScroll)
        {
            return;
        }
        StopScrolling();
        UpdateScrolling();
    }

    private void Control_OnPointerExited(object? sender, PointerEventArgs e)
    {
        textBlock.ClearHighlight();
        if (textBlock.HasAnnotations)
        {
            UpdateScrolling();
            return;
        }
        if (!AutoScroll)
        {
            StopScrolling();
        }
    }

    /// <inheritdoc />
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var horizontal = e.Delta.X != 0 ? e.Delta.X :
            e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? e.Delta.Y : 0;
        if (textBlock.HasAnnotations && horizontal != 0)
        {
            UpdateViewport();
            controller.Seek(controller.Offset - horizontal * FontSize * 3);
            ApplyOffset();
            textBlock.UpdateHighlight(e.GetPosition(textBlock));
            e.Handled = true;
        }
        base.OnPointerWheelChanged(e);
    }

    private void Timer_OnTick(object? sender, EventArgs e)
    {
        if (!CanAnimate)
        {
            UpdateScrolling();
            return;
        }
        var elapsed = stopwatch.Elapsed;
        stopwatch.Restart();
        UpdateViewport();
        controller.Advance(elapsed, AutoScroll);
        ApplyOffset();

        if (!controller.CanScroll || !AutoScroll && controller.IsComplete)
        {
            timer.Stop();
            stopwatch.Stop();
        }
    }

    private void ScrollViewer_OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != ScrollViewer.ExtentProperty && e.Property != ScrollViewer.ViewportProperty)
        {
            return;
        }

        UpdateScrolling();
    }

    private void Ancestor_OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty)
        {
            Dispatcher.UIThread.Post(UpdateScrolling, DispatcherPriority.Loaded);
        }
    }

    private void UpdateScrolling()
    {
        if (!IsAnimationAvailable)
        {
            StopScrolling();
            textBlock.ClearHighlight();
            return;
        }

        if (!CanAnimate)
        {
            if (textBlock.HasAnnotations)
            {
                PauseScrolling();
            }
            else
            {
                StopScrolling();
            }
            return;
        }

        UpdateViewport();
        ApplyOffset();
        if (!controller.CanScroll || !AutoScroll && controller.IsComplete)
        {
            timer.Stop();
            stopwatch.Stop();
            return;
        }

        if (timer.IsEnabled)
        {
            return;
        }

        stopwatch.Restart();
        timer.Start();
    }

    private void UpdateViewport()
    {
        controller.UpdateViewport(scrollViewer.Extent.Width, scrollViewer.Viewport.Width);
    }

    private void ApplyOffset()
    {
        scrollViewer.Offset = new(controller.Offset, 0);
    }

    private void StopScrolling()
    {
        timer.Stop();
        stopwatch.Reset();
        controller.Reset();
        ApplyOffset();
    }

    private void PauseScrolling()
    {
        timer.Stop();
        stopwatch.Reset();
    }
}
