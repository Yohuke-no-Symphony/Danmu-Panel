using System.Globalization;
using Yohuke.DanmuPanel.Services.Translation;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Translation;

/// <summary>验证正文注音保留整句读音上下文和原文文本元素的位置。</summary>
public sealed class ContentPinyinServiceTests
{
    /// <summary>多音词按整句转换，逐字结果保留原文位置。</summary>
    [Theory]
    [InlineData("重庆银行", "chóng,qìng,yín,háng")]
    [InlineData("重慶銀行", "chóng,qìng,yín,háng")]
    public void PhraseContextDeterminesEachCharacterReading(string text, string expected)
    {
        var annotations = new PinyinService().GetAnnotations(text);

        Assert.Equal(expected.Split(','), annotations.Select(annotation => annotation.Pinyin));
        Assert.Equal(Enumerable.Range(0, text.Length), annotations.Select(annotation => annotation.SourceStart));
        Assert.All(annotations, annotation => Assert.Equal(1, annotation.SourceLength));
        Assert.Equal(text, string.Concat(annotations.Select(annotation => annotation.Text)));
    }

    /// <summary>混排内容无丢失，emoji 和组合字符不会被切开。</summary>
    [Theory]
    [InlineData("你好ABC 123，👨‍👩‍👧‍👦!\r\n世界e\u0301")]
    [InlineData("中文かな カナ ｶﾅ 中文")]
    [InlineData("你好\U00020000❤️ 中\uFE00文")]
    public void MixedContentPreservesCompleteTextElementsAndSourceRanges(string text)
    {
        var annotations = new PinyinService().GetAnnotations(text);
        var boundaries = StringInfo.ParseCombiningCharacters(text).Append(text.Length).ToHashSet();
        var offset = 0;

        Assert.NotEmpty(annotations);
        foreach (var annotation in annotations)
        {
            Assert.Equal(offset, annotation.SourceStart);
            Assert.Contains(offset, boundaries);
            offset += annotation.SourceLength;
            Assert.Contains(offset, boundaries);
            if (annotation.HasPinyin)
            {
                Assert.Equal(1, new StringInfo(annotation.Text).LengthInTextElements);
            }
        }
        Assert.Equal(text.Length, offset);
        Assert.Equal(text, string.Concat(annotations.Select(annotation => annotation.Text)));
    }

    /// <summary>正文中的假名不会沿用姓名策略屏蔽整句汉字。</summary>
    [Fact]
    public void KanaDoesNotSuppressOtherChineseContent()
    {
        var annotations = new PinyinService().GetAnnotations("你好かな");

        Assert.Equal(["nǐ", "hǎo"], annotations.Where(annotation => annotation.HasPinyin)
            .Select(annotation => annotation.Pinyin));
        Assert.Equal("かな", string.Concat(annotations.Where(annotation => !annotation.HasPinyin)
            .Select(annotation => annotation.Text)));
    }

    /// <summary>复杂非汉字前缀之后的汉字仍保持正确读音和 UTF-16 索引。</summary>
    [Fact]
    public void ChineseReadingsAfterEmojiAndCombiningCharactersKeepTheirSourceOffsets()
    {
        const string prefix = "👨‍👩‍👧‍👦e\u0301❤️\r\nABC ";
        var annotations = new PinyinService().GetAnnotations(prefix + "你好");
        var chinese = annotations.Where(annotation => annotation.HasPinyin).ToArray();

        Assert.Equal(["nǐ", "hǎo"], chinese.Select(annotation => annotation.Pinyin));
        Assert.Equal([prefix.Length, prefix.Length + 1], chinese.Select(annotation => annotation.SourceStart));
    }

    /// <summary>没有可转换汉字时不创建注音显示。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("Alice123，👨‍👩‍👧‍👦\r\n")]
    [InlineData("かな カナ ｶﾅ")]
    [InlineData("\U00020000")]
    public void ContentWithoutSupportedChineseHasNoAnnotations(string text)
    {
        Assert.Empty(new PinyinService().GetAnnotations(text));
    }

    /// <summary>缓存结果不可被调用者修改，重复读取复用同一结果。</summary>
    [Fact]
    public void AnnotationCacheIsSharedAndReadOnly()
    {
        var service = new PinyinService();
        var annotations = service.GetAnnotations("你好");

        Assert.NotEmpty(annotations);
        Assert.Same(annotations, service.GetAnnotations("你好"));
        Assert.True(Assert.IsAssignableFrom<System.Collections.IList>(annotations).IsReadOnly);
    }
}
