using Avalonia.Media;
using Avalonia.Media.Immutable;
using Xunit;
using Yohuke.DanmuPanel.Services.Appearance;

namespace Yohuke.DanmuPanel.Tests.Appearance;

/// <summary>验证通用文字对比度计算及透明颜色合成。</summary>
public sealed class ColorContrastTests
{
    /// <summary>不满足对比度的首选文字色会切换为对比度更高的黑色或白色。</summary>
    [Theory]
    [InlineData("White", "White", "Black")]
    [InlineData("Black", "Black", "White")]
    [InlineData("#C8EBFA", "#627087", "Black")]
    [InlineData("#102030", "#345678", "White")]
    [InlineData("White", "#80000000", "Black")]
    public void UnreadablePreferredColorsFallBack(string background, string preferred, string expected)
    {
        var foreground = ColorContrast.ResolveForeground(Color.Parse(background), new ImmutableSolidColorBrush(Color.Parse(preferred)));

        Assert.Equal(Color.Parse(expected), Assert.IsAssignableFrom<ISolidColorBrush>(foreground).Color);
        Assert.True(ColorContrast.GetContrastRatio(Assert.IsAssignableFrom<ISolidColorBrush>(foreground).Color,
            Color.Parse(background)) >= ColorContrast.MINIMUM_TEXT_CONTRAST);
    }

    /// <summary>可读的自定义文字色与透明文字色保留原画刷，而不是强制覆盖用户偏好。</summary>
    [Theory]
    [InlineData("#FEDCBA", "#345678", 1)]
    [InlineData("White", "Black", 0.8)]
    public void ReadablePreferencesKeepTheirBrush(string background, string foreground, double opacity)
    {
        var preferred = new ImmutableSolidColorBrush(Color.Parse(foreground), opacity);

        Assert.Same(preferred, ColorContrast.ResolveForeground(Color.Parse(background), preferred));
    }

    /// <summary>颜色 Alpha 和画刷透明度均参与背景合成。</summary>
    [Theory]
    [InlineData("#80FFFFFF", 1, "Black", "#808080")]
    [InlineData("#80000000", 1, "White", "#7F7F7F")]
    [InlineData("Black", 0.1, "White", "#E6E6E6")]
    [InlineData("#80000000", 0.5, "White", "#BFBFBF")]
    [InlineData("Transparent", 1, "#123456", "#123456")]
    public void BackgroundCompositionIncludesBothAlphaSources(string foreground, double opacity,
        string background, string expected)
    {
        var brush = new ImmutableSolidColorBrush(Color.Parse(foreground), opacity);

        Assert.Equal(Color.Parse(expected), ColorContrast.Composite(brush, Color.Parse(background)));
    }

    /// <summary>黑白文字的对比度上限为 21，相同颜色的对比度为 1。</summary>
    [Fact]
    public void ContrastRatioUsesRelativeLuminance()
    {
        Assert.Equal(21, ColorContrast.GetContrastRatio(Colors.Black, Colors.White), 6);
        Assert.Equal(1, ColorContrast.GetContrastRatio(Colors.White, Colors.White), 6);
    }
}
