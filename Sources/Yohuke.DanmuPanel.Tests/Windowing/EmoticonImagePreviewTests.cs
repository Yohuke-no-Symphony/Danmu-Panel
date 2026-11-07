using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Models.Workspace;
using Yohuke.DanmuPanel.Views.Components;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Windowing;

/// <summary>验证内联表情放大、悬停大图、滚动命中和预览图片的释放。</summary>
public sealed class EmoticonImagePreviewTests
{
    /// <summary>普通和紧凑正文均按比例显示 28 像素表情，并按需提供独立的 160 像素预览。</summary>
    [AvaloniaTheory]
    [InlineData(false, 640, 320)]
    [InlineData(true, 640, 320)]
    [InlineData(false, 320, 640)]
    [InlineData(true, 320, 640)]
    public async Task InlineAndTooltipPreserveTheImageAspectRatio(bool marquee, int width, int height)
    {
        var path = CreateImageFile(width, height);
        var content = CreateContent(marquee, path);
        var window = CreateWindow(content);
        Bitmap? lastPreview = null;
        try
        {
            window.Show();
            await EmoticonImageTestHelper.LoadAsync(window);
            var text = FindText(content);
            var inline = FindInlineImage(text);
            var small = Assert.IsType<Bitmap>(inline.Source);
            Assert.InRange(Math.Abs(inline.Bounds.Height - 28), 0, 0.1);
            Assert.InRange(Math.Abs(inline.Bounds.Width / inline.Bounds.Height - (double)width / height), 0, 0.01);
            Assert.False(ToolTip.GetIsOpen(PreviewHost(inline)));

            var preview = await OpenPreviewAsync(window, inline);
            var large = Assert.IsType<Bitmap>(preview.Source);
            Assert.NotSame(small, large);
            Assert.True(Math.Max(large.PixelSize.Width, large.PixelSize.Height) >
                Math.Max(small.PixelSize.Width, small.PixelSize.Height));
            Assert.InRange(Math.Abs(Math.Max(preview.Bounds.Width, preview.Bounds.Height) - 160), 0, 0.5);
            Assert.InRange(Math.Abs(preview.Bounds.Width / preview.Bounds.Height - (double)width / height), 0, 0.01);

            window.MouseMove(new(window.Width - 8, window.Height - 8));
            await EmoticonImageTestHelper.LoadAsync(window);
            Assert.False(ToolTip.GetIsOpen(PreviewHost(inline)));
            Assert.Null(preview.Source);
            AssertDisposed(large);
            Assert.True(small.PixelSize.Height > 0);

            var reopened = await OpenPreviewAsync(window, inline);
            lastPreview = Assert.IsType<Bitmap>(reopened.Source);
            Assert.NotSame(large, lastPreview);
            Assert.True(lastPreview.PixelSize.Height > 0);
        }
        finally
        {
            window.Close();
            File.Delete(path);
        }
        Assert.NotNull(lastPreview);
        AssertDisposed(lastPreview);
    }

    /// <summary>表情高度继承共同祖先，字号及高度改变后普通和紧凑分支均更新现有布局。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmoticonHeightInheritsAndUpdatesWithTypography(bool marquee)
    {
        var path = CreateImageFile(640, 320);
        var content = CreateContent(marquee, path);
        var host = new Border { Child = content };
        host.SetValue(EmoticonTextBlock.EmoticonHeightProperty, 42d);
        var window = CreateWindow(host);
        try
        {
            window.Show();
            await EmoticonImageTestHelper.LoadAsync(window);
            var text = FindText(content);
            Assert.Equal(42, text.EmoticonHeight);
            Assert.InRange(Math.Abs(FindInlineImage(text).Bounds.Height - 42), 0, 0.1);

            if (content is HoverMarqueeTextBlock scrolling)
            {
                scrolling.FontSize = 26;
            }
            else
            {
                text.FontSize = 26;
            }
            host.SetValue(EmoticonTextBlock.EmoticonHeightProperty, 56d);
            await EmoticonImageTestHelper.LoadAsync(window);

            var inline = FindInlineImage(text);
            Assert.Equal(26, text.FontSize);
            Assert.Equal(56, text.EmoticonHeight);
            Assert.InRange(Math.Abs(inline.Bounds.Height - 56), 0, 0.1);
            Assert.InRange(Math.Abs(inline.Bounds.Width - 112), 0, 0.1);
            Assert.True(Assert.IsType<Bitmap>(inline.Source).PixelSize.Height >= 56);
            var preview = await OpenPreviewAsync(window, inline);
            Assert.InRange(Math.Abs(Math.Max(preview.Bounds.Width, preview.Bounds.Height) - 160), 0, 0.5);
        }
        finally
        {
            window.Close();
            File.Delete(path);
        }
    }

    /// <summary>离开、隐藏、回收和关闭都会关闭预览并释放大图，恢复后可以重新载入。</summary>
    [AvaloniaTheory]
    [InlineData(false, "exit")]
    [InlineData(true, "hidden")]
    [InlineData(false, "ancestor")]
    [InlineData(true, "dataContext")]
    [InlineData(false, "content")]
    [InlineData(true, "detach")]
    [InlineData(true, "window")]
    public async Task PreviewClosesAndReleasesItsBitmapWhenTheOwnerChanges(bool marquee, string change)
    {
        var path = CreateImageFile(320, 320);
        var content = CreateContent(marquee, path);
        var host = new Border { Child = content };
        var window = CreateWindow(host);
        try
        {
            window.Show();
            await EmoticonImageTestHelper.LoadAsync(window);
            var inline = FindInlineImage(FindText(content));
            var previewHost = PreviewHost(inline);
            var preview = await OpenPreviewAsync(window, inline);
            var bitmap = Assert.IsType<Bitmap>(preview.Source);

            switch (change)
            {
                case "exit":
                    window.MouseMove(new(window.Width - 8, window.Height - 8));
                    break;
                case "hidden":
                    content.IsVisible = false;
                    break;
                case "ancestor":
                    host.IsVisible = false;
                    break;
                case "dataContext":
                    content.DataContext = new object();
                    break;
                case "content":
                    SetText(content, "已经换成另一条消息");
                    break;
                case "detach":
                    host.Child = null;
                    break;
                case "window":
                    window.Close();
                    break;
            }
            await EmoticonImageTestHelper.LoadAsync(window);

            Assert.False(ToolTip.GetIsOpen(previewHost));
            Assert.Null(preview.Source);
            AssertDisposed(bitmap);
            if (change != "window")
            {
                content.IsVisible = true;
                host.IsVisible = true;
                host.Child = content;
                SetText(content, "[笑]");
                await EmoticonImageTestHelper.LoadAsync(window);
                window.MouseMove(new(window.Width - 8, window.Height - 8));
                var restored = await OpenPreviewAsync(window, FindInlineImage(FindText(content)));
                Assert.NotSame(bitmap, restored.Source);
                Assert.True(Assert.IsType<Bitmap>(restored.Source).PixelSize.Height > 0);
            }
        }
        finally
        {
            window.Close();
            File.Delete(path);
        }
    }

    /// <summary>预览图片不消耗消息行点击，离开图片后仍能准确高亮两种排版的后续拼音。</summary>
    [AvaloniaTheory]
    [InlineData(PinyinLayoutMode.Ruby, false)]
    [InlineData(PinyinLayoutMode.SeparateLine, true)]
    public async Task PreviewPreservesPinyinHoverAndParentButtonClicks(PinyinLayoutMode layout, bool marquee)
    {
        var path = CreateImageFile(320, 320);
        const string message = "你[笑]好";
        var content = CreateContent(marquee, path, message);
        if (content is HoverMarqueeTextBlock scrolling)
        {
            scrolling.Annotations = PinyinTextRenderingTests.Annotate(message);
            scrolling.PinyinLayout = layout;
        }
        else
        {
            var original = Assert.IsType<EmoticonTextBlock>(content);
            original.Annotations = PinyinTextRenderingTests.Annotate(message);
            original.PinyinLayout = layout;
        }
        var button = new Button
        {
            Content = content,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top
        };
        var clicks = 0;
        button.Click += (_, _) => clicks++;
        var window = CreateWindow(button);
        try
        {
            window.Show();
            await EmoticonImageTestHelper.LoadAsync(window);
            var text = FindText(content);
            var inline = FindInlineImage(text);
            await OpenPreviewAsync(window, inline);
            var sourceStart = message.IndexOf('好');
            Point point;
            if (layout == PinyinLayoutMode.Ruby)
            {
                var syllable = Assert.Single(text.GetVisualDescendants().OfType<PinyinRubySyllable>(),
                    value => value.Annotation.SourceStart == sourceStart);
                point = Center(syllable.OriginalText, window);
            }
            else
            {
                var original = Assert.Single(text.GetVisualDescendants().OfType<PinyinSeparateLinePresenter>()).OriginalText;
                point = original.TranslatePoint(original.TextLayout.HitTestTextPosition(2).Center, window)!.Value;
            }
            window.MouseMove(point);
            await EmoticonImageTestHelper.LoadAsync(window);

            Assert.Equal(sourceStart, text.HighlightedSourceStart);
            Assert.False(ToolTip.GetIsOpen(PreviewHost(inline)));
            var imagePoint = Center(inline, window);
            window.MouseMove(imagePoint);
            window.MouseDown(imagePoint, MouseButton.Left);
            window.MouseUp(imagePoint, MouseButton.Left);
            await EmoticonImageTestHelper.LoadAsync(window);
            Assert.Equal(1, clicks);
        }
        finally
        {
            window.Close();
            File.Delete(path);
        }
    }

    /// <summary>自动滚动到图片已有偏移时仍能显示预览，悬停期间暂停并在离开后继续。</summary>
    [AvaloniaFact]
    public async Task ScrolledEmoticonPreviewPausesAndResumesTheMarquee()
    {
        var path = CreateImageFile(320, 320);
        var marquee = Assert.IsType<HoverMarqueeTextBlock>(CreateContent(true, path, "[笑]" + new string('长', 80)));
        marquee.Width = 220;
        marquee.AutoScroll = true;
        var window = CreateWindow(marquee);
        try
        {
            window.Show();
            window.MouseMove(new(window.Width - 8, window.Height - 8));
            await EmoticonImageTestHelper.LoadAsync(window);
            var viewer = Assert.IsType<ScrollViewer>(marquee.Content);
            await WaitUntilAsync(() => viewer.Offset.X > 3, window);
            var inline = FindInlineImage(FindText(marquee));
            var preview = await OpenPreviewAsync(window, inline);
            var bitmap = Assert.IsType<Bitmap>(preview.Source);
            var offset = viewer.Offset.X;

            await Task.Delay(120);
            await EmoticonImageTestHelper.LoadAsync(window);

            Assert.Equal(offset, viewer.Offset.X);
            Assert.True(ToolTip.GetIsOpen(PreviewHost(inline)));
            window.MouseMove(new(window.Width - 8, window.Height - 8));
            await EmoticonImageTestHelper.LoadAsync(window);
            Assert.False(ToolTip.GetIsOpen(PreviewHost(inline)));
            Assert.Null(preview.Source);
            AssertDisposed(bitmap);
            await WaitUntilAsync(() => viewer.Offset.X > offset + 1, window);
        }
        finally
        {
            window.Close();
            File.Delete(path);
        }
    }

    /// <summary>预览期间点击图片不会吞掉父按钮动作，关闭后原文字提示及其绑定继续生效。</summary>
    [AvaloniaFact]
    public async Task PreviewRestoresTheHostsTooltipBindingAndPreservesDirectImageClicks()
    {
        var path = CreateImageFile(320, 320);
        var content = CreateContent(true, path);
        content.DataContext = "原来的文字提示";
        using var binding = content.Bind(ToolTip.TipProperty, new Binding("."));
        var button = new Button
        {
            Content = content,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top
        };
        var clicks = 0;
        button.Click += (_, _) => clicks++;
        var window = CreateWindow(button);
        try
        {
            window.Show();
            await EmoticonImageTestHelper.LoadAsync(window);
            Assert.Equal("原来的文字提示", ToolTip.GetTip(content));
            Assert.True(ToolTip.GetServiceEnabled(content));
            var inline = FindInlineImage(FindText(content));
            var preview = await OpenPreviewAsync(window, inline);
            var bitmap = Assert.IsType<Bitmap>(preview.Source);
            var point = Center(inline, window);

            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            await EmoticonImageTestHelper.LoadAsync(window);

            Assert.Equal(1, clicks);
            Assert.False(ToolTip.GetIsOpen(content));
            Assert.Null(preview.Source);
            AssertDisposed(bitmap);
            Assert.Equal("原来的文字提示", ToolTip.GetTip(content));
            Assert.True(ToolTip.GetServiceEnabled(content));
            window.MouseMove(new(window.Width - 8, window.Height - 8));
            content.DataContext = "数据更新后的文字提示";
            await EmoticonImageTestHelper.LoadAsync(window);
            Assert.Equal("数据更新后的文字提示", ToolTip.GetTip(content));
        }
        finally
        {
            window.Close();
            File.Delete(path);
        }
    }

    /// <summary>预览等待显示期间隐藏正文或更换消息后，过期定时器不能打开旧图或重新构建内容。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingPreviewIsCancelledBeforeItsDelayWhenTheContentChanges(bool hide)
    {
        var path = CreateImageFile(320, 320);
        var content = CreateContent(true, path);
        var window = CreateWindow(content);
        try
        {
            window.Show();
            await EmoticonImageTestHelper.LoadAsync(window);
            var text = FindText(content);
            var inline = FindInlineImage(text);
            var bitmap = Assert.IsType<Bitmap>(inline.Source);
            ToolTip.SetShowDelay(inline, 200);
            window.MouseMove(Center(inline, window));
            await EmoticonImageTestHelper.LoadAsync(window);
            Assert.True(text.HasHoveredEmoticon);
            Assert.False(ToolTip.GetIsOpen(content));

            if (hide)
            {
                content.IsVisible = false;
            }
            else
            {
                SetText(content, "另一条没有表情的消息");
            }
            await EmoticonImageTestHelper.LoadAsync(window);
            Assert.False(text.HasHoveredEmoticon);
            Assert.Empty(text.GetVisualDescendants().OfType<Image>());
            AssertDisposed(bitmap);

            await Task.Delay(260);
            await EmoticonImageTestHelper.LoadAsync(window);

            Assert.False(ToolTip.GetIsOpen(content));
            Assert.Null(ToolTip.GetTip(content));
            Assert.False(text.HasHoveredEmoticon);
            Assert.Empty(text.GetVisualDescendants().OfType<Image>());
            AssertDisposed(bitmap);
        }
        finally
        {
            window.Close();
            File.Delete(path);
        }
    }

    private static Control CreateContent(bool marquee, string path, string message = "[笑]")
    {
        var emoticons = new[] { new DanmuEmoticon { Text = "[笑]", ImagePath = path } };
        if (marquee)
        {
            return new HoverMarqueeTextBlock
            {
                Text = message,
                Emoticons = emoticons,
                AutoScroll = false,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Top
            };
        }
        return new EmoticonTextBlock
        {
            ContentText = message,
            Emoticons = emoticons,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Top
        };
    }

    private static EmoticonTextBlock FindText(Control content)
    {
        if (content is EmoticonTextBlock text)
        {
            return text;
        }
        return Assert.IsType<EmoticonTextBlock>(Assert.IsType<ScrollViewer>(Assert.IsType<HoverMarqueeTextBlock>(content).Content).Content);
    }

    private static Image FindInlineImage(EmoticonTextBlock text)
    {
        return Assert.Single(text.GetVisualDescendants().OfType<Image>());
    }

    private static async Task<Image> OpenPreviewAsync(Window window, Image inline)
    {
        var host = PreviewHost(inline);
        ToolTip.SetShowDelay(inline, 0);
        window.MouseMove(Center(inline, window));
        await EmoticonImageTestHelper.LoadAsync(window);
        Assert.True(ToolTip.GetIsOpen(host));
        return Assert.IsType<Image>(Assert.IsType<ToolTip>(ToolTip.GetTip(host)).Content);
    }

    private static Control PreviewHost(Image inline)
    {
        var ancestors = inline.GetVisualAncestors().ToArray();
        return ancestors.OfType<HoverMarqueeTextBlock>().FirstOrDefault() as Control ??
            ancestors.OfType<EmoticonTextBlock>().First();
    }

    private static Point Center(Control control, Window window)
    {
        Assert.True(control.Bounds.Width > 0 && control.Bounds.Height > 0);
        return control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
    }

    private static void SetText(Control content, string value)
    {
        if (content is HoverMarqueeTextBlock marquee)
        {
            marquee.Text = value;
        }
        else
        {
            Assert.IsType<EmoticonTextBlock>(content).ContentText = value;
        }
    }

    private static string CreateImageFile(int width, int height)
    {
        var path = Path.Combine(Path.GetTempPath(), $"emoticon-preview-{Guid.NewGuid():N}.png");
        using var bitmap = new RenderTargetBitmap(new(width, height), new(96, 96));
        bitmap.Save(path);
        return path;
    }

    private static Window CreateWindow(Control content)
    {
        return new()
        {
            Width = 400,
            Height = 300,
            Content = new Border { Padding = new(12), Child = content }
        };
    }

    private static void AssertDisposed(Bitmap bitmap)
    {
        Assert.Throws<NullReferenceException>(() => bitmap.PixelSize);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, Window window)
    {
        for (var attempt = 0; attempt < 150; attempt++)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(10);
            await EmoticonImageTestHelper.LoadAsync(window);
        }
        Assert.Fail("The marquee did not reach the expected scroll offset.");
    }

}
