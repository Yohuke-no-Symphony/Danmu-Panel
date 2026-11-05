using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Models.Workspace;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views.Components;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Windowing;

/// <summary>验证不可见文本不创建富文本内容，重新显示时同步恢复最新绑定与排版。</summary>
public sealed class EmoticonTextVisibilityTests
{
    /// <summary>纯文本不因祖先显隐重新构建，隐藏期间启用、关闭及再次启用注音后仍同步恢复。</summary>
    [AvaloniaFact]
    public void PlainTextVisibilityDoesNotRebuildAndHiddenRichTransitionsRecover()
    {
        var text = new EmoticonTextBlock { ContentText = "你好" };
        var host = new Border { Child = text, IsVisible = false };
        var window = new Window { Width = 360, Height = 180, Content = host };
        try
        {
            window.Show();
            PinyinTextRenderingTests.Pump(window);
            var rebuilds = 0;
            text.AnnotationLayoutChanged += () => rebuilds++;
            host.IsVisible = true;
            host.IsVisible = false;
            Assert.Equal(0, rebuilds);

            text.Annotations = PinyinTextRenderingTests.Annotate("你好");
            AssertDeferred(text, "你好");
            host.IsVisible = true;
            Assert.True(text.HasAnnotations);

            text.Annotations = [];
            AssertDeferred(text, "你好");
            rebuilds = 0;
            host.IsVisible = false;
            host.IsVisible = true;
            Assert.Equal(0, rebuilds);
            host.IsVisible = false;
            text.Annotations = PinyinTextRenderingTests.Annotate("你好");
            AssertDeferred(text, "你好");
            host.IsVisible = true;
            Assert.True(text.HasAnnotations);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>隐藏控件或祖先后更新绑定和字号，不创建注音树，显示操作返回前恢复最新内容。</summary>
    [AvaloniaTheory]
    [InlineData(PinyinLayoutMode.Ruby, false)]
    [InlineData(PinyinLayoutMode.Ruby, true)]
    [InlineData(PinyinLayoutMode.SeparateLine, false)]
    [InlineData(PinyinLayoutMode.SeparateLine, true)]
    public void HiddenBindingChangesAreRenderedSynchronouslyWhenShown(PinyinLayoutMode layout, bool hideAncestor)
    {
        var text = new EmoticonTextBlock { PinyinLayout = layout };
        text.Bind(EmoticonTextBlock.ContentTextProperty, new Binding("Content"));
        text.Bind(EmoticonTextBlock.AnnotationsProperty, new Binding("Annotations"));
        text.DataContext = new { Content = "你好", Annotations = PinyinTextRenderingTests.Annotate("你好") };
        var host = new Border { Child = text };
        var visibility = hideAncestor ? (Control)host : text;
        visibility.IsVisible = false;
        var window = new Window { Width = 360, Height = 180, Content = host };
        try
        {
            window.Show();
            PinyinTextRenderingTests.Pump(window);
            AssertDeferred(text, "你好");

            text.DataContext = new { Content = "好你", Annotations = PinyinTextRenderingTests.Annotate("好你") };
            text.PinyinLayout = layout == PinyinLayoutMode.Ruby ? PinyinLayoutMode.SeparateLine : PinyinLayoutMode.Ruby;
            text.PinyinLayout = layout;
            text.FontSize = 28;
            text.PinyinFontSize = 14;
            text.PinyinForeground = Brushes.DarkOrange;
            AssertDeferred(text, "好你");

            visibility.IsVisible = true;

            Assert.True(text.HasAnnotations);
            Assert.Equal(-1, text.HighlightedSourceStart);
            if (layout == PinyinLayoutMode.Ruby)
            {
                var syllables = text.Inlines!.OfType<InlineUIContainer>()
                    .Select(inline => Assert.IsType<PinyinRubySyllable>(inline.Child)).ToArray();
                Assert.Equal(new[] { "好", "你" }, syllables.Select(syllable => syllable.OriginalText.Text));
                Assert.All(syllables, syllable =>
                {
                    Assert.Equal(28, syllable.OriginalText.FontSize);
                    Assert.Equal(14, syllable.PronunciationText.FontSize);
                    Assert.Same(Brushes.DarkOrange, syllable.PronunciationText.Foreground);
                });
            }
            else
            {
                var presenter = Assert.IsType<PinyinSeparateLinePresenter>(Assert.Single(text.Inlines!.OfType<InlineUIContainer>()).Child);
                Assert.Equal("好你", presenter.OriginalText.Inlines!.Text);
                Assert.Equal(28, presenter.OriginalText.FontSize);
                Assert.Equal(14, presenter.PronunciationText.FontSize);
                Assert.Same(Brushes.DarkOrange, presenter.PronunciationText.Foreground);
            }

            visibility.IsVisible = false;
            AssertDeferred(text, "好你");
            Assert.Equal(-1, text.HighlightedSourceStart);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>本地重新显示不能越过仍然隐藏的祖先，只有整个可见链恢复后才创建内容。</summary>
    [AvaloniaFact]
    public void LocalVisibilityChangesRespectHiddenAncestors()
    {
        var text = new EmoticonTextBlock
        {
            ContentText = "你好", Annotations = PinyinTextRenderingTests.Annotate("你好")
        };
        var inner = new Border { Child = text };
        var outer = new Border { Child = inner };
        var window = new Window { Width = 360, Height = 180, Content = outer };
        try
        {
            window.Show();
            PinyinTextRenderingTests.Pump(window);
            Assert.True(text.HasAnnotations);

            outer.IsVisible = false;
            AssertDeferred(text, "你好");
            inner.IsVisible = false;
            text.IsVisible = false;
            text.IsVisible = true;
            outer.IsVisible = true;
            AssertDeferred(text, "你好");
            inner.IsVisible = true;
            Assert.True(text.HasAnnotations);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>脱离旧祖先时释放内容，再挂载到隐藏的新祖先后仅由新的可见性恢复当前内容。</summary>
    [AvaloniaFact]
    public void DetachedTextRebuildsOnlyUnderItsCurrentVisibleAncestors()
    {
        var text = new EmoticonTextBlock
        {
            ContentText = "你好", Annotations = PinyinTextRenderingTests.Annotate("你好")
        };
        var oldHost = new Border { Child = text };
        var newHost = new Border { IsVisible = false };
        var window = new Window { Width = 360, Height = 180, Content = new Grid { Children = { oldHost, newHost } } };
        try
        {
            window.Show();
            PinyinTextRenderingTests.Pump(window);
            Assert.True(text.HasAnnotations);
            oldHost.Child = null;
            Assert.False(text.HasAnnotations);
            Assert.Empty(text.Inlines!);
            text.ContentText = "好你";
            text.Annotations = PinyinTextRenderingTests.Annotate("好你");
            AssertDeferred(text, "好你");
            oldHost.IsVisible = false;
            oldHost.IsVisible = true;
            AssertDeferred(text, "好你");

            newHost.Child = text;
            AssertDeferred(text, "好你");
            newHost.IsVisible = true;
            Assert.True(text.HasAnnotations);
            oldHost.IsVisible = false;
            Assert.True(text.HasAnnotations);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>隐藏表情正文不解码图片，显示时同步载入，隐藏时释放，下一次显示重新载入。</summary>
    [AvaloniaFact]
    public void HiddenEmoticonsAreDecodedWhenShownAndReleasedWhenHidden()
    {
        var imagePath = Path.Combine(Path.GetTempPath(), $"visibility-emoticon-{Guid.NewGuid():N}.png");
        using (var bitmap = new RenderTargetBitmap(new(8, 8), new(96, 96)))
        {
            bitmap.Save(imagePath);
        }
        var text = new EmoticonTextBlock
        {
            ContentText = "[笑]", Emoticons = [new DanmuEmoticon { Text = "[笑]", ImagePath = imagePath }]
        };
        var host = new Border { IsVisible = false, Child = text };
        var window = new Window { Width = 360, Height = 180, Content = host };
        try
        {
            window.Show();
            PinyinTextRenderingTests.Pump(window);
            AssertDeferred(text, "[笑]");

            host.IsVisible = true;
            var image = Assert.IsType<Image>(Assert.Single(text.Inlines!.OfType<InlineUIContainer>()).Child);
            var decoded = Assert.IsType<Bitmap>(image.Source);
            Assert.True(decoded.PixelSize.Height > 0);

            host.IsVisible = false;
            AssertDeferred(text, "[笑]");
            Assert.Throws<NullReferenceException>(() => decoded.PixelSize);

            host.IsVisible = true;
            var restored = Assert.IsType<Image>(Assert.Single(text.Inlines!.OfType<InlineUIContainer>()).Child);
            Assert.NotSame(decoded, restored.Source);
        }
        finally
        {
            window.Close();
            File.Delete(imagePath);
        }
    }

    /// <summary>消息行并存的普通和紧凑正文只为可见分支创建注音，模式切换恢复正确内容。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void MessageRowsBuildAnnotationsOnlyForTheVisibleBody(bool compact)
    {
        var row = new MessageFeedRow(MessageKind.Danmu)
        {
            DataContext = new MessageItemViewModel(new()
            {
                Kind = MessageKind.Danmu, UserName = "观众", Content = "你好世界"
            }, new() { ShowDanmuPinyin = true, AutoScrollLongMessages = false }, new PinyinService())
        };
        var host = new Grid { Children = { row } };
        if (compact)
        {
            host.Classes.Add("compactDanmu");
        }
        var window = new Window { Width = 600, Height = 240, Content = host };
        try
        {
            window.Show();
            PinyinTextRenderingTests.Pump(window);
            var regular = Assert.Single(row.GetVisualDescendants().OfType<EmoticonTextBlock>(),
                block => block.Classes.Contains("regularFeedContent"));
            var marquee = Assert.Single(row.GetVisualDescendants().OfType<HoverMarqueeTextBlock>(),
                block => block.Classes.Contains("originalText"));
            var marqueeText = Assert.IsType<EmoticonTextBlock>(Assert.IsType<ScrollViewer>(marquee.Content).Content);
            Assert.Equal(!compact, regular.HasAnnotations);
            Assert.Equal(compact, marqueeText.HasAnnotations);

            host.Classes.Set("compactDanmu", !compact);
            PinyinTextRenderingTests.Pump(window);

            Assert.Equal(compact, regular.HasAnnotations);
            Assert.Equal(!compact, marqueeText.HasAnnotations);
            Assert.Equal("你好世界", compact ? marqueeText.Text : regular.Text);
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertDeferred(EmoticonTextBlock text, string content)
    {
        Assert.False(text.HasAnnotations);
        Assert.Null(text.Inlines);
        Assert.Equal(content, text.Text);
    }
}
