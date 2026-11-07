using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;

namespace Yohuke.DanmuPanel.Views.Components;

/// <summary>通过独立内容插槽共享搜索与手动输入的弹窗布局，不管理业务请求。</summary>
public sealed partial class SearchAndManualDialogSurface : UserControl
{
    public static readonly StyledProperty<Control?> HeaderContentProperty =
        AvaloniaProperty.Register<SearchAndManualDialogSurface, Control?>(nameof(HeaderContent));
    public static readonly StyledProperty<Control?> SharedContentProperty =
        AvaloniaProperty.Register<SearchAndManualDialogSurface, Control?>(nameof(SharedContent));
    public static readonly StyledProperty<Control?> SearchContentProperty =
        AvaloniaProperty.Register<SearchAndManualDialogSurface, Control?>(nameof(SearchContent));
    public static readonly StyledProperty<Control?> ManualContentProperty =
        AvaloniaProperty.Register<SearchAndManualDialogSurface, Control?>(nameof(ManualContent));
    public static readonly StyledProperty<Control?> StatusContentProperty =
        AvaloniaProperty.Register<SearchAndManualDialogSurface, Control?>(nameof(StatusContent));
    public static readonly StyledProperty<Control?> FooterContentProperty =
        AvaloniaProperty.Register<SearchAndManualDialogSurface, Control?>(nameof(FooterContent));
    public static readonly StyledProperty<bool> IsSearchEnabledProperty =
        AvaloniaProperty.Register<SearchAndManualDialogSurface, bool>(nameof(IsSearchEnabled), true);
    public static readonly StyledProperty<SearchAndManualDialogMode> SelectedModeProperty =
        AvaloniaProperty.Register<SearchAndManualDialogSurface, SearchAndManualDialogMode>(nameof(SelectedMode),
            defaultBindingMode: BindingMode.TwoWay,
            validate: static mode => Enum.IsDefined(mode),
            coerce: static (owner, mode) => mode == SearchAndManualDialogMode.Search && !((SearchAndManualDialogSurface)owner).IsSearchEnabled
                ? SearchAndManualDialogMode.Manual
                : mode);

    private readonly TabControl modeTabs;
    private readonly TabItem automaticTab;
    private readonly TabItem manualTab;
    private readonly bool initialized;
    private bool synchronizingSelection;

    public Control? HeaderContent
    {
        get => GetValue(HeaderContentProperty);
        set => SetValue(HeaderContentProperty, value);
    }

    public Control? SharedContent
    {
        get => GetValue(SharedContentProperty);
        set => SetValue(SharedContentProperty, value);
    }

    public Control? SearchContent
    {
        get => GetValue(SearchContentProperty);
        set => SetValue(SearchContentProperty, value);
    }

    public Control? ManualContent
    {
        get => GetValue(ManualContentProperty);
        set => SetValue(ManualContentProperty, value);
    }

    public Control? StatusContent
    {
        get => GetValue(StatusContentProperty);
        set => SetValue(StatusContentProperty, value);
    }

    public Control? FooterContent
    {
        get => GetValue(FooterContentProperty);
        set => SetValue(FooterContentProperty, value);
    }

    public bool IsSearchEnabled
    {
        get => GetValue(IsSearchEnabledProperty);
        set => SetValue(IsSearchEnabledProperty, value);
    }

    public SearchAndManualDialogMode SelectedMode
    {
        get => GetValue(SelectedModeProperty);
        set => SetValue(SelectedModeProperty, value);
    }

    /// <summary>加载固定的两个输入页签，并同步初始选择状态。</summary>
    public SearchAndManualDialogSurface()
    {
        InitializeComponent();
        modeTabs = this.FindControl<TabControl>("SearchAndManualTabs")!;
        automaticTab = this.FindControl<TabItem>("AutomaticSearchTab")!;
        manualTab = this.FindControl<TabItem>("ManualEntryTab")!;
        initialized = true;
        SynchronizeSelectedTab();
    }

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (!initialized)
        {
            return;
        }
        if (change.Property == IsSearchEnabledProperty)
        {
            if (!IsSearchEnabled)
            {
                SetCurrentValue(SelectedModeProperty, SearchAndManualDialogMode.Manual);
            }
            CoerceValue(SelectedModeProperty);
            SynchronizeSelectedTab();
        }
        else if (change.Property == SelectedModeProperty)
        {
            SynchronizeSelectedTab();
        }
    }

    private void Modes_OnSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (!initialized || synchronizingSelection || !ReferenceEquals(args.Source, modeTabs))
        {
            return;
        }
        if (ReferenceEquals(modeTabs.SelectedItem, automaticTab))
        {
            SetCurrentValue(SelectedModeProperty, SearchAndManualDialogMode.Search);
        }
        else if (ReferenceEquals(modeTabs.SelectedItem, manualTab))
        {
            SetCurrentValue(SelectedModeProperty, SearchAndManualDialogMode.Manual);
        }
        SynchronizeSelectedTab();
    }

    private void SynchronizeSelectedTab()
    {
        var selected = SelectedMode == SearchAndManualDialogMode.Manual ? manualTab : automaticTab;
        if (ReferenceEquals(modeTabs.SelectedItem, selected))
        {
            return;
        }
        synchronizingSelection = true;
        try
        {
            modeTabs.SelectedItem = selected;
        }
        finally
        {
            synchronizingSelection = false;
        }
    }
}
