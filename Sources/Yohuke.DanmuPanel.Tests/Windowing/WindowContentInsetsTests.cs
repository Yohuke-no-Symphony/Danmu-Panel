using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Yohuke.DanmuPanel.Windowing;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Windowing;

/// <summary>
/// 验证原生边框补偿的布局、宿主边距、绑定和释放生命周期。
/// </summary>
public sealed class WindowContentInsetsTests
{
    /// <summary>
    /// 重复最大化、DPI 变化与还原不会累计移动标题栏或改变显式客户区尺寸。
    /// </summary>
    [AvaloniaFact]
    public void RepeatedInsetsMoveTitleBarWithoutAccumulatingOrChangingClientSize()
    {
        var window = new Window
        {
            Width = 400,
            Height = 300,
            Padding = new(3, 5, 7, 11),
            Content = new TextBox()
        };
        var titleBar = WindowChrome.AttachFrame(window);
        try
        {
            var contentRoot = (Control)window.Content!;
            using var insets = new WindowContentInsets(contentRoot);
            window.Show();
            Pump(window);
            var originalPosition = titleBar.TranslatePoint(default, window)!.Value;
            var originalClientSize = window.ClientSize;
            var offsets = new[] { 8.0, 8, 0, 12, 0, 8, 0 };
            foreach (var offset in offsets)
            {
                insets.Update(new(0, offset, 0, 0));
                Pump(window);

                Assert.Equal(new Thickness(3, 5, 7, 11), window.Padding);
                Assert.Equal(new Thickness(0, offset, 0, 0), contentRoot.Margin);
                Assert.Equal(originalPosition.Y + offset, titleBar.TranslatePoint(default, window)!.Value.Y);
                Assert.Equal(originalClientSize, window.ClientSize);
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// 宿主更新边距后继续叠加本层补偿，并在释放时恢复最新值。
    /// </summary>
    [AvaloniaFact]
    public void HostPaddingChangesRemainIndependentOfFrameCompensation()
    {
        var window = new Window
        {
            Padding = new(1, 2, 3, 4),
            Content = new Border()
        };
        try
        {
            var contentRoot = (Control)window.Content!;
            using var insets = new WindowContentInsets(contentRoot);
            insets.Update(new(0, 8, 0, 0));
            window.Padding = new(10, 20, 30, 40);

            Assert.Equal(new Thickness(10, 20, 30, 40), window.Padding);
            Assert.Equal(new Thickness(0, 8, 0, 0), contentRoot.Margin);
            insets.Update(default);
            Assert.Equal(new Thickness(10, 20, 30, 40), window.Padding);
            insets.Update(new(0, 12, 0, 0));
            insets.Dispose();
            Assert.Equal(new Thickness(10, 20, 30, 40), window.Padding);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// 边距绑定在补偿更新、宿主变化与释放之后继续生效。
    /// </summary>
    [AvaloniaTheory]
    [InlineData(BindingMode.OneWay)]
    [InlineData(BindingMode.TwoWay)]
    public void PaddingBindingSurvivesInsetsAndDisposal(BindingMode mode)
    {
        var source = new Border
        {
            Padding = new(1, 2, 3, 4)
        };
        var contentRoot = new Border();
        var window = new Window
        {
            Content = contentRoot
        };
        try
        {
            using var binding = window.Bind(TemplatedControl.PaddingProperty, new Binding(nameof(Border.Padding))
            {
                Source = source,
                Mode = mode
            });
            using var insets = new WindowContentInsets(contentRoot);
            insets.Update(new(0, 8, 0, 0));
            Assert.Equal(new Thickness(1, 2, 3, 4), window.Padding);
            Assert.Equal(new Thickness(1, 2, 3, 4), source.Padding);
            Assert.Equal(new Thickness(0, 8, 0, 0), contentRoot.Margin);

            source.Padding = new(1, 10, 3, 4);
            Assert.Equal(source.Padding, window.Padding);
            insets.Update(new(0, 12, 0, 0));
            Assert.Equal(new Thickness(1, 10, 3, 4), source.Padding);
            Assert.Equal(source.Padding, window.Padding);
            Assert.Equal(new Thickness(0, 12, 0, 0), contentRoot.Margin);

            insets.Dispose();
            Assert.Equal(default, contentRoot.Margin);
            Assert.Equal(source.Padding, window.Padding);
            source.Padding = new(9, 10, 11, 12);
            Assert.Equal(source.Padding, window.Padding);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// 不同窗口的补偿互不影响，释放后恢复原有内容边距并拒绝后续更新。
    /// </summary>
    [AvaloniaFact]
    public void WindowInsetsRemainIndependentAndStopUpdatingAfterDisposal()
    {
        var firstRoot = new Border
        {
            Margin = new(1)
        };
        var secondRoot = new Border
        {
            Margin = new(2)
        };
        var first = new Window
        {
            Content = firstRoot
        };
        var second = new Window
        {
            Content = secondRoot
        };
        try
        {
            using var firstInsets = new WindowContentInsets(firstRoot);
            using var secondInsets = new WindowContentInsets(secondRoot);
            firstInsets.Update(new(0, 8, 0, 0));
            secondInsets.Update(new(0, 12, 0, 0));

            Assert.Equal(new Thickness(1, 9, 1, 1), firstRoot.Margin);
            Assert.Equal(new Thickness(2, 14, 2, 2), secondRoot.Margin);
            firstInsets.Dispose();
            Assert.Equal(new Thickness(1), firstRoot.Margin);
            first.Padding = new(5);
            Assert.Equal(new Thickness(5), first.Padding);
            Assert.Equal(new Thickness(2, 14, 2, 2), secondRoot.Margin);
            Assert.Throws<ObjectDisposedException>(() => firstInsets.Update(new(0, 8, 0, 0)));
            Assert.Equal(new Thickness(5), first.Padding);
        }
        finally
        {
            first.Close();
            second.Close();
        }
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
