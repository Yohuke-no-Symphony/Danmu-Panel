using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Xunit;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Tests.Appearance;

/// <summary>验证通用自适应容器响应背景层、画刷变化和视觉树复用。</summary>
public sealed class AdaptiveForegroundBorderTests
{
    /// <summary>透明容器跟随窗口实际底色和深浅主题变化，而不依赖消息显示模型。</summary>
    [AvaloniaFact]
    public void WindowBackgroundAndThemeChangesUpdateForeground()
    {
        var badge = new AdaptiveForegroundBorder { Background = Brushes.Transparent };
        var window = new Window
        {
            Background = Brushes.White, RequestedThemeVariant = ThemeVariant.Light,
            Content = badge, Width = 200, Height = 100
        };
        try
        {
            window.Show();
            Pump(window);
            AssertForeground("Black", badge);

            window.Background = Brushes.Black;
            Pump(window);
            AssertForeground("White", badge);

            window.ClearValue(Window.BackgroundProperty);
            window.RequestedThemeVariant = ThemeVariant.Dark;
            Pump(window);
            AssertForeground("White", badge);

            window.RequestedThemeVariant = ThemeVariant.Light;
            Pump(window);
            AssertForeground("Black", badge);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>透明容器按实际父级背景选色，并跟随画刷和背景属性变化。</summary>
    [AvaloniaFact]
    public void TransparentLayersTrackParentColorsAndBrushOpacity()
    {
        var backdrop = new SolidColorBrush(Colors.White);
        var overlay = new SolidColorBrush(Colors.Black, 0.1);
        var badge = new AdaptiveForegroundBorder { Background = overlay };
        var panel = new Border { Background = backdrop, Child = badge };
        var window = new Window { Content = panel, Width = 200, Height = 100 };
        try
        {
            window.Show();
            Pump(window);
            AssertColor("#E6E6E6", badge.EffectiveBackground);
            AssertForeground("Black", badge);

            backdrop.Color = Colors.Black;
            Pump(window);
            AssertColor("Black", badge.EffectiveBackground);
            AssertForeground("White", badge);

            backdrop.Color = Colors.White;
            overlay.Opacity = 1;
            Pump(window);
            AssertForeground("White", badge);

            badge.Background = Brushes.White;
            Pump(window);
            AssertForeground("Black", badge);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>自定义文字色改变为不可读颜色时重新选色，恢复可读后复用原画刷。</summary>
    [AvaloniaFact]
    public void PreferredBrushMutationsReevaluateReadability()
    {
        var preferred = new SolidColorBrush(Colors.Black);
        var badge = new AdaptiveForegroundBorder { Background = Brushes.White, PreferredForeground = preferred };
        var window = new Window { Content = badge, Width = 200, Height = 100 };
        try
        {
            window.Show();
            Pump(window);
            Assert.Same(preferred, badge.Foreground);

            preferred.Color = Colors.White;
            Pump(window);
            AssertForeground("Black", badge);

            preferred.Color = Colors.Black;
            preferred.Opacity = 0.1;
            Pump(window);
            AssertForeground("Black", badge);
            Assert.NotSame(preferred, badge.Foreground);

            preferred.Opacity = 1;
            Pump(window);
            Assert.Same(preferred, badge.Foreground);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>同一容器移到其他背景后重建观察，离开旧父级后不再受旧背景影响。</summary>
    [AvaloniaFact]
    public void ReparentingUsesTheNewBackground()
    {
        var badge = new AdaptiveForegroundBorder { Background = Brushes.Transparent };
        var oldBrush = new SolidColorBrush(Colors.White);
        var first = new Border { Background = oldBrush, Child = badge };
        var second = new Border { Background = Brushes.Black };
        var window = new Window { Content = new StackPanel { Children = { first, second } }, Width = 200, Height = 100 };
        try
        {
            window.Show();
            Pump(window);
            AssertForeground("Black", badge);

            first.Child = null;
            second.Child = badge;
            Pump(window);
            AssertForeground("White", badge);

            oldBrush.Color = Colors.Blue;
            Pump(window);
            AssertForeground("White", badge);
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertForeground(string expected, AdaptiveForegroundBorder badge)
    {
        AssertColor(expected, Assert.IsAssignableFrom<ISolidColorBrush>(badge.Foreground).Color);
    }

    private static void AssertColor(string expected, Color actual)
    {
        Assert.Equal(Color.Parse(expected), actual);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
