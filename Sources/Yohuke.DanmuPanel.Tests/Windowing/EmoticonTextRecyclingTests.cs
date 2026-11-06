using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Tests.Windowing;

/// <summary>验证虚拟化换绑合并正文与注音的更新，并同步恢复完整的新消息。</summary>
public sealed class EmoticonTextRecyclingTests
{
    /// <summary>回收后的注音节点正常离开视觉树，下一条消息复用节点并更新原文、拼音及来源位置。</summary>
    [AvaloniaFact]
    public void RubyNodesAreDetachedAndReusedForTheCurrentMessage()
    {
        var pinyin = new PinyinService();
        var text = new EmoticonTextBlock();
        text.Bind(EmoticonTextBlock.ContentTextProperty, new Binding("Content"));
        text.Bind(EmoticonTextBlock.AnnotationsProperty, new Binding("Annotations"));
        var host = new Border { Child = text };
        host.DataContext = new { Content = "你好", Annotations = pinyin.GetAnnotations("你好") };
        var window = new Window { Width = 360, Height = 180, Content = host };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var originals = RubyNodes(text);

            host.DataContext = null;

            Assert.False(text.HasAnnotations);
            Assert.All(originals, node =>
            {
                Assert.Null(node.GetVisualRoot());
                Assert.Null(Assert.IsType<InlineUIContainer>(node.Parent).Parent);
            });
            host.DataContext = new { Content = "晚安", Annotations = pinyin.GetAnnotations("晚安") };

            var restored = RubyNodes(text);
            Assert.Equal(originals, restored);
            Assert.Equal(new[] { "晚", "安" }, restored.Select(node => node.OriginalText.Text));
            Assert.Equal(new[] { 0, 1 }, restored.Select(node => node.Annotation.SourceStart));
            Assert.All(restored, node => Assert.Equal(node.Annotation.Pinyin, node.PronunciationText.Text));
            window.UpdateLayout();
            Assert.All(restored, node => Assert.Same(window, node.GetVisualRoot()));

            host.Child = null;
            Assert.All(restored, node => Assert.Null(node.GetVisualRoot()));
            host.Child = text;
            window.UpdateLayout();
            Assert.Equal(restored, RubyNodes(text));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>长消息离开可视树后仅保留有限注音节点，后续短消息仍然完整且没有旧节点混入。</summary>
    [AvaloniaFact]
    public void LongMessagesLeaveABoundedRubyCache()
    {
        var pinyin = new PinyinService();
        var content = new string('好', 128);
        var text = new EmoticonTextBlock { ContentText = content, Annotations = pinyin.GetAnnotations(content) };
        var host = new Border { Child = text };
        var window = new Window { Width = 360, Height = 180, Content = host };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Equal(128, RubyNodes(text).Length);

            host.Child = null;

            Assert.InRange(text.CachedRubyInlineCount, 1, 64);
            Assert.False(text.HasAnnotations);
            text.ContentText = "晚安";
            text.Annotations = pinyin.GetAnnotations("晚安");
            host.Child = text;
            window.UpdateLayout();
            Assert.Equal(new[] { "晚", "安" }, RubyNodes(text).Select(node => node.OriginalText.Text));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>直接或继承的数据上下文一次换绑只重建一次，返回时已经可以测量和显示最新注音。</summary>
    [AvaloniaTheory]
    [InlineData(PinyinLayoutMode.Ruby, false)]
    [InlineData(PinyinLayoutMode.Ruby, true)]
    [InlineData(PinyinLayoutMode.SeparateLine, false)]
    [InlineData(PinyinLayoutMode.SeparateLine, true)]
    public void ContextRebindingBuildsOnlyTheCompletedMessage(PinyinLayoutMode layout, bool inheritContext)
    {
        var pinyin = new PinyinService();
        var text = new EmoticonTextBlock { PinyinLayout = layout };
        text.Bind(EmoticonTextBlock.ContentTextProperty, new Binding("Content"));
        text.Bind(EmoticonTextBlock.AnnotationsProperty, new Binding("Annotations"));
        var host = new Border { Child = text };
        var contextOwner = inheritContext ? (Control)host : text;
        contextOwner.DataContext = new { Content = "你好", Annotations = pinyin.GetAnnotations("你好") };
        var window = new Window { Width = 360, Height = 180, Content = host };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(text.HasAnnotations);
            var rebuilds = 0;
            text.AnnotationLayoutChanged += () => rebuilds++;

            contextOwner.DataContext = new { Content = "晚安", Annotations = pinyin.GetAnnotations("晚安") };

            Assert.True(text.HasAnnotations);
            Assert.Equal("晚安", text.ContentText);
            if (layout == PinyinLayoutMode.Ruby)
            {
                Assert.Equal("晚安", string.Concat(text.Inlines!.OfType<InlineUIContainer>()
                    .Select(inline => Assert.IsType<PinyinRubySyllable>(inline.Child).OriginalText.Text)));
            }
            else
            {
                var presenter = Assert.IsType<PinyinSeparateLinePresenter>(Assert.Single(text.Inlines!.OfType<InlineUIContainer>()).Child);
                Assert.Equal("晚安", presenter.OriginalText.Inlines!.Text);
            }
            Assert.Equal(1, rebuilds);

            text.ContentText = "你好";
            text.Annotations = pinyin.GetAnnotations("你好");
            Assert.True(text.HasAnnotations);
            Assert.Equal(3, rebuilds);
        }
        finally
        {
            window.Close();
        }
    }

    private static PinyinRubySyllable[] RubyNodes(EmoticonTextBlock text)
    {
        return text.Inlines!.OfType<InlineUIContainer>().Select(inline =>
            Assert.IsType<PinyinRubySyllable>(inline.Child)).ToArray();
    }
}
