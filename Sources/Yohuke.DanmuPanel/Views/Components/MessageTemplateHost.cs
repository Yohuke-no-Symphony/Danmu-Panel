using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed class MessageTemplateHost : Decorator
{
    public static readonly StyledProperty<bool> IsContentActiveProperty =
        AvaloniaProperty.Register<MessageTemplateHost, bool>(nameof(IsContentActive));

    public static readonly StyledProperty<bool> UseMarqueeProperty =
        AvaloniaProperty.Register<MessageTemplateHost, bool>(nameof(UseMarquee));

    public static readonly StyledProperty<IDataTemplate?> StandardTemplateProperty =
        AvaloniaProperty.Register<MessageTemplateHost, IDataTemplate?>(nameof(StandardTemplate));

    public static readonly StyledProperty<IDataTemplate?> MarqueeTemplateProperty =
        AvaloniaProperty.Register<MessageTemplateHost, IDataTemplate?>(nameof(MarqueeTemplate));

    private IDataTemplate? activeTemplate;
    private Control? recycledChild;
    private bool isDataContextUpdating;

    public bool IsContentActive
    {
        get => GetValue(IsContentActiveProperty);
        set => SetValue(IsContentActiveProperty, value);
    }

    public bool UseMarquee
    {
        get => GetValue(UseMarqueeProperty);
        set => SetValue(UseMarqueeProperty, value);
    }

    public IDataTemplate? StandardTemplate
    {
        get => GetValue(StandardTemplateProperty);
        set => SetValue(StandardTemplateProperty, value);
    }

    public IDataTemplate? MarqueeTemplate
    {
        get => GetValue(MarqueeTemplateProperty);
        set => SetValue(MarqueeTemplateProperty, value);
    }

    internal event Action<Control>? PreparingChild;
    internal event Action<Control>? RemovingChild;

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (!isDataContextUpdating && (change.Property == IsContentActiveProperty || change.Property == UseMarqueeProperty ||
            change.Property == StandardTemplateProperty || change.Property == MarqueeTemplateProperty ||
            change.Property == DataContextProperty))
        {
            UpdateTemplate();
        }
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
        UpdateTemplate();
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is null && DataContext is not null && activeTemplate is { } template)
        {
            var child = recycledChild ?? template.Build(null)!;
            recycledChild = null;
            child.ClearValue(DataContextProperty);
            PreparingChild?.Invoke(child);
            Child = child;
        }
        return base.MeasureOverride(availableSize);
    }

    private void UpdateTemplate()
    {
        if (DataContext is null)
        {
            if (Child is { } retiring)
            {
                recycledChild = retiring;
                RemoveChild(retiring);
            }
            return;
        }

        var template = IsContentActive
            ? (UseMarquee ? MarqueeTemplate ?? StandardTemplate : StandardTemplate)
            : null;
        if (ReferenceEquals(activeTemplate, template))
        {
            if (recycledChild is not null)
            {
                InvalidateMeasure();
            }
            return;
        }

        activeTemplate = template;
        recycledChild = null;
        if (Child is { } retired)
        {
            RemoveChild(retired);
        }
        InvalidateMeasure();
    }

    private void RemoveChild(Control child)
    {
        RemovingChild?.Invoke(child);
        Child = null;
    }
}
