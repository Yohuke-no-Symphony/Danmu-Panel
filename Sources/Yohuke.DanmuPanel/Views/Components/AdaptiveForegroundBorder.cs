using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.Services.Appearance;

namespace Yohuke.DanmuPanel.Views.Components;

/// <summary>根据自身及父级绘制表面的纯色背景，为子控件提供可读的文字画刷。</summary>
public sealed class AdaptiveForegroundBorder : Border
{
    /// <summary>配置的首选文字色；对比度不足时自动改用黑色或白色。</summary>
    public static readonly StyledProperty<IBrush?> PreferredForegroundProperty =
        AvaloniaProperty.Register<AdaptiveForegroundBorder, IBrush?>(nameof(PreferredForeground));

    /// <summary>根据合成背景计算的只读文字画刷。</summary>
    public static readonly DirectProperty<AdaptiveForegroundBorder, IBrush> ForegroundProperty =
        AvaloniaProperty.RegisterDirect<AdaptiveForegroundBorder, IBrush>(nameof(Foreground), border => border.Foreground);

    private readonly List<Visual> backgroundAncestors = [];
    private readonly HashSet<AvaloniaObject> observedBrushes = [];
    private IBrush foreground = Brushes.Black;
    private bool isAttached;

    /// <summary>获取或设置希望优先使用的文字画刷。</summary>
    public IBrush? PreferredForeground
    {
        get => GetValue(PreferredForegroundProperty);
        set => SetValue(PreferredForegroundProperty, value);
    }

    /// <summary>可供任意子控件绑定的自适应文字画刷。</summary>
    public IBrush Foreground => foreground;

    internal Color EffectiveBackground { get; private set; }

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(Border);

    /// <summary>创建随当前主题变化更新文字对比度的背景容器。</summary>
    public AdaptiveForegroundBorder()
    {
        ActualThemeVariantChanged += OnActualThemeVariantChanged;
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        isAttached = true;
        foreach (var ancestor in this.GetVisualAncestors())
        {
            if (ancestor is Border or Panel or ContentPresenter or TextBlock or TopLevel)
            {
                backgroundAncestors.Add(ancestor);
                ancestor.PropertyChanged += OnAncestorPropertyChanged;
            }
        }
        ObserveBrushes();
        UpdateForeground();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        isAttached = false;
        foreach (var ancestor in backgroundAncestors)
        {
            ancestor.PropertyChanged -= OnAncestorPropertyChanged;
        }
        backgroundAncestors.Clear();
        ClearBrushObservers();
        base.OnDetachedFromVisualTree(e);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BackgroundProperty || change.Property == PreferredForegroundProperty)
        {
            if (isAttached)
            {
                ObserveBrushes();
            }
            UpdateForeground();
        }
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs e)
    {
        UpdateForeground();
    }

    private void OnAncestorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property.Name == nameof(Background))
        {
            ObserveBrushes();
            UpdateForeground();
        }
    }

    private void ObserveBrushes()
    {
        ClearBrushObservers();
        ObserveBrush(Background);
        ObserveBrush(PreferredForeground);
        foreach (var ancestor in backgroundAncestors)
        {
            ObserveBrush(GetBackground(ancestor));
        }
    }

    private void ObserveBrush(IBrush? brush)
    {
        if (brush is AvaloniaObject observable && observedBrushes.Add(observable))
        {
            observable.PropertyChanged += OnBrushPropertyChanged;
        }
    }

    private void ClearBrushObservers()
    {
        foreach (var brush in observedBrushes)
        {
            brush.PropertyChanged -= OnBrushPropertyChanged;
        }
        observedBrushes.Clear();
    }

    private void OnBrushPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == SolidColorBrush.ColorProperty || change.Property == Brush.OpacityProperty)
        {
            UpdateForeground();
        }
    }

    private void UpdateForeground()
    {
        var background = ActualThemeVariant == ThemeVariant.Dark ? Colors.Black : Colors.White;
        for (var index = backgroundAncestors.Count - 1; index >= 0; index--)
        {
            if (GetBackground(backgroundAncestors[index]) is ISolidColorBrush brush)
            {
                background = ColorContrast.Composite(brush, background);
            }
        }
        if (Background is ISolidColorBrush ownBackground)
        {
            background = ColorContrast.Composite(ownBackground, background);
        }
        EffectiveBackground = background;
        SetAndRaise(ForegroundProperty, ref foreground,
            ColorContrast.ResolveForeground(background, PreferredForeground as ISolidColorBrush));
    }

    private static IBrush? GetBackground(Visual visual)
    {
        return visual switch
        {
            Border border => border.Background,
            Panel panel => panel.Background,
            ContentPresenter presenter => presenter.Background,
            TextBlock text => text.Background,
            TopLevel topLevel => topLevel.Background,
            _ => null
        };
    }
}
