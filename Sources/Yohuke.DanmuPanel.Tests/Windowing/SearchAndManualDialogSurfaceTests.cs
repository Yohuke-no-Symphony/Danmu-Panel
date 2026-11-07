using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Yohuke.DanmuPanel.Views.Components;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Windowing;

/// <summary>验证共享查找弹窗的插槽、模式同步及搜索入口开关。</summary>
public sealed class SearchAndManualDialogSurfaceTests
{
    /// <summary>默认显示自动搜索，保留宿主数据上下文并隐藏未提供的可选插槽。</summary>
    [AvaloniaFact]
    public void DefaultModePreservesHostDataContextAndHidesEmptySlots()
    {
        var context = new object();
        var search = new TextBox { Text = "搜索草稿" };
        var surface = new SearchAndManualDialogSurface { DataContext = context, SearchContent = search };
        var window = new Window { Content = surface, Width = 640, Height = 520 };
        try
        {
            window.Show();
            Pump(window);

            Assert.Equal(SearchAndManualDialogMode.Search, surface.SelectedMode);
            Assert.Same(surface.FindControl<TabItem>("AutomaticSearchTab"), surface.FindControl<TabControl>("SearchAndManualTabs")!.SelectedItem);
            Assert.True(search.IsEffectivelyVisible);
            Assert.Same(context, search.DataContext);
            Assert.False(surface.FindControl<ContentPresenter>("HeaderContentHost")!.IsVisible);
            Assert.False(surface.FindControl<ContentPresenter>("SharedContentHost")!.IsVisible);
            Assert.False(surface.FindControl<ContentPresenter>("StatusContentHost")!.IsVisible);
            Assert.False(surface.FindControl<ContentPresenter>("FooterContentHost")!.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>公共模式属性与页签双向同步，并通过双向绑定更新外部状态。</summary>
    [AvaloniaFact]
    public void ModePropertyAndUiSelectionSynchronizeInBothDirections()
    {
        var source = new SearchAndManualDialogSurface();
        var surface = new SearchAndManualDialogSurface();
        using var binding = surface.Bind(SearchAndManualDialogSurface.SelectedModeProperty,
            new Binding(nameof(SearchAndManualDialogSurface.SelectedMode)) { Source = source, Mode = BindingMode.TwoWay });
        var window = new Window { Content = surface, Width = 640, Height = 520 };
        try
        {
            window.Show();
            source.SelectedMode = SearchAndManualDialogMode.Manual;
            Pump(window);
            var tabs = surface.FindControl<TabControl>("SearchAndManualTabs")!;
            Assert.Same(surface.FindControl<TabItem>("ManualEntryTab"), tabs.SelectedItem);

            tabs.SelectedItem = surface.FindControl<TabItem>("AutomaticSearchTab");
            Pump(window);

            Assert.Equal(SearchAndManualDialogMode.Search, surface.SelectedMode);
            Assert.Equal(SearchAndManualDialogMode.Search, source.SelectedMode);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>禁用自动搜索时立即转到手动输入，程序设置和界面选择均不能停留在禁用页。</summary>
    [AvaloniaFact]
    public void DisablingSearchForcesManualModeAndDisablesAutomaticTab()
    {
        var surface = new SearchAndManualDialogSurface();
        var window = new Window { Content = surface, Width = 640, Height = 520 };
        try
        {
            window.Show();
            surface.IsSearchEnabled = false;
            Pump(window);
            var searchTab = surface.FindControl<TabItem>("AutomaticSearchTab")!;
            var manualTab = surface.FindControl<TabItem>("ManualEntryTab")!;
            var tabs = surface.FindControl<TabControl>("SearchAndManualTabs")!;
            Assert.False(searchTab.IsEnabled);
            Assert.True(manualTab.IsEnabled);
            Assert.Equal(SearchAndManualDialogMode.Manual, surface.SelectedMode);
            Assert.Same(manualTab, tabs.SelectedItem);

            surface.SelectedMode = SearchAndManualDialogMode.Search;
            tabs.SelectedItem = searchTab;
            Pump(window);

            Assert.Equal(SearchAndManualDialogMode.Manual, surface.SelectedMode);
            Assert.Same(manualTab, tabs.SelectedItem);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>插槽内部选择事件冒泡时不会覆盖外层页签状态。</summary>
    [AvaloniaFact]
    public void NestedSelectionChangesDoNotChangeDialogMode()
    {
        var choice = new ComboBox { ItemsSource = new[] { "first", "second" } };
        var surface = new SearchAndManualDialogSurface { ManualContent = choice, SelectedMode = SearchAndManualDialogMode.Manual };
        var window = new Window { Content = surface, Width = 640, Height = 520 };
        try
        {
            window.Show();
            Pump(window);

            choice.SelectedIndex = 1;
            Pump(window);

            Assert.Equal("second", choice.SelectedItem);
            Assert.Equal(SearchAndManualDialogMode.Manual, surface.SelectedMode);
            Assert.Same(surface.FindControl<TabItem>("ManualEntryTab"), surface.FindControl<TabControl>("SearchAndManualTabs")!.SelectedItem);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>切换页签复用同一组控件与输入内容，并为所有插槽继承宿主上下文。</summary>
    [AvaloniaFact]
    public void SwitchingModesKeepsInputsAndSlotInstances()
    {
        var context = new object();
        var header = new TextBlock { Text = "标题" };
        var shared = new TextBox { Text = "共用资料" };
        var search = new TextBox { Text = "搜索资料" };
        var manual = new TextBox { Text = "手动资料" };
        var status = new TextBlock { Text = "提示" };
        var footer = new Button { Content = "完成" };
        var surface = new SearchAndManualDialogSurface
        {
            DataContext = context,
            HeaderContent = header,
            SharedContent = shared,
            SearchContent = search,
            ManualContent = manual,
            StatusContent = status,
            FooterContent = footer
        };
        var window = new Window { Content = surface, Width = 640, Height = 520 };
        try
        {
            window.Show();
            Pump(window);
            surface.SelectedMode = SearchAndManualDialogMode.Manual;
            Pump(window);
            Assert.True(manual.IsEffectivelyVisible);
            Assert.Same(context, manual.DataContext);
            manual.Text = "继续编辑";
            surface.SelectedMode = SearchAndManualDialogMode.Search;
            Pump(window);

            Assert.True(search.IsEffectivelyVisible);
            Assert.Equal("搜索资料", search.Text);
            Assert.Equal("继续编辑", manual.Text);
            Assert.Same(search, surface.SearchContent);
            Assert.Same(manual, surface.ManualContent);
            Assert.All(new Control[] { header, shared, search, status, footer }, control =>
            {
                Assert.True(control.IsEffectivelyVisible);
                Assert.Same(context, control.DataContext);
            });
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>小窗口中的长手动表单可滚动至末尾，底栏保持独立可访问。</summary>
    [AvaloniaFact]
    public void LongManualFormScrollsToLastControlInSmallWindow()
    {
        var manual = new StackPanel { Spacing = 10 };
        for (var index = 0; index < 14; index++)
        {
            manual.Children.Add(new TextBox { Text = "字段 " + index, Height = 40 });
        }
        var last = new Button { Content = "提交资料", Height = 40 };
        manual.Children.Add(last);
        var footer = new Button { Content = "关闭", Height = 40 };
        var surface = new SearchAndManualDialogSurface
        {
            HeaderContent = new TextBlock { Text = "标题", Height = 40 },
            SharedContent = new TextBox { Text = "账号", Height = 40 },
            ManualContent = manual,
            StatusContent = new TextBlock { Text = "提示", Height = 24 },
            FooterContent = footer,
            SelectedMode = SearchAndManualDialogMode.Manual
        };
        var window = new Window { Content = surface, Width = 480, Height = 420 };
        try
        {
            window.Show();
            Pump(window);
            var viewer = surface.FindControl<ScrollViewer>("ManualContentScrollViewer");
            Assert.NotNull(viewer);
            Assert.True(viewer.Extent.Height > viewer.Viewport.Height);
            Assert.True(viewer.Viewport.Height > last.Bounds.Height);

            last.BringIntoView();
            Pump(window);

            Assert.True(viewer.Offset.Y > 0);
            var lastPosition = last.TranslatePoint(default, viewer);
            Assert.NotNull(lastPosition);
            Assert.InRange(lastPosition.Value.Y, -1, viewer.Viewport.Height - last.Bounds.Height + 1);
            Assert.True(footer.IsEffectivelyVisible);
            var footerPosition = footer.TranslatePoint(default, surface);
            Assert.NotNull(footerPosition);
            Assert.InRange(footerPosition.Value.Y + footer.Bounds.Height, 0, surface.Bounds.Height);
        }
        finally
        {
            window.Close();
        }
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
