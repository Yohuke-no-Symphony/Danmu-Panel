using Avalonia.Media;

namespace Yohuke.DanmuPanel.Services.Appearance;

/// <summary>合成纯色背景层，并按相对亮度为文字选择可读颜色。</summary>
public static class ColorContrast
{
    /// <summary>普通字号文字使用的最低对比度。</summary>
    public const double MINIMUM_TEXT_CONTRAST = 4.5;

    /// <summary>将纯色画刷叠加在背景上，同时考虑颜色 Alpha 和画刷透明度。</summary>
    public static Color Composite(ISolidColorBrush foreground, Color background)
    {
        return Composite(foreground.Color, foreground.Opacity, background);
    }

    /// <summary>计算文字叠加到不透明背景后的相对亮度对比度。</summary>
    public static double GetContrastRatio(Color foreground, Color background)
    {
        var renderedForeground = Composite(foreground, 1, background);
        var foregroundLuminance = GetLuminance(renderedForeground);
        var backgroundLuminance = GetLuminance(background);
        return (Math.Max(foregroundLuminance, backgroundLuminance) + 0.05) /
            (Math.Min(foregroundLuminance, backgroundLuminance) + 0.05);
    }

    /// <summary>保留满足对比度的首选文字色，否则选择对比度更高的黑色或白色。</summary>
    public static IBrush ResolveForeground(Color background, ISolidColorBrush? preferredForeground = null)
    {
        if (preferredForeground is not null &&
            GetContrastRatio(Composite(preferredForeground, background), background) >= MINIMUM_TEXT_CONTRAST)
        {
            return preferredForeground;
        }
        return GetContrastRatio(Colors.Black, background) >= GetContrastRatio(Colors.White, background)
            ? Brushes.Black : Brushes.White;
    }

    private static Color Composite(Color foreground, double opacity, Color background)
    {
        var foregroundAlpha = foreground.A / 255d * opacity;
        var backgroundAlpha = background.A / 255d;
        var remainingBackground = backgroundAlpha * (1 - foregroundAlpha);
        var alpha = foregroundAlpha + remainingBackground;
        if (alpha == 0)
        {
            return Colors.Transparent;
        }
        return Color.FromArgb(ToByte(alpha * 255),
            ToByte((foreground.R * foregroundAlpha + background.R * remainingBackground) / alpha),
            ToByte((foreground.G * foregroundAlpha + background.G * remainingBackground) / alpha),
            ToByte((foreground.B * foregroundAlpha + background.B * remainingBackground) / alpha));
    }

    private static byte ToByte(double value)
    {
        return (byte)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);
    }

    private static double GetLuminance(Color color)
    {
        return 0.2126 * Linearize(color.R) + 0.7152 * Linearize(color.G) + 0.0722 * Linearize(color.B);
    }

    private static double Linearize(byte channel)
    {
        var value = channel / 255d;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }
}
