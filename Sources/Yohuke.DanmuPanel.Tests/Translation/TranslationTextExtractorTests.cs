using Xunit;
using Yohuke.DanmuPanel.Services.Translation;

namespace Yohuke.DanmuPanel.Tests.Translation;

public sealed class TranslationTextExtractorTests
{
    [Theory]
    [InlineData("😀👍🏽🇯🇵", "")]
    [InlineData("👨‍👩‍👦 1️⃣", "")]
    [InlineData("你好🙂 world", "你好 world")]
    [InlineData("[不是表情] 保留", "[不是表情] 保留")]
    [InlineData("Hello\n  world", "Hello world")]
    [InlineData("東京 123", "東京 123")]
    public void ExtractRemovesEmojiWithoutDiscardingOtherText(string source, string expected)
    {
        Assert.Equal(expected, TranslationTextExtractor.Extract(source));
    }

    [Fact]
    public void ProtocolEmoticonsAreRemovedByExactKnownToken()
    {
        Assert.Equal("你好 [未知]", TranslationTextExtractor.Extract("你好[笑][未知]", ["[笑]"]));
        Assert.Equal("", TranslationTextExtractor.Extract("[笑][笑]", ["[笑]"]));
    }

    /// <summary>仅表情和空白属于纯表情；文字、数字、标点和未知标记均保留。</summary>
    [Theory]
    [InlineData("😀👍🏽🇯🇵", true)]
    [InlineData("👨‍👩‍👦 1️⃣", true)]
    [InlineData("  [笑][笑]\n🙂  ", true)]
    [InlineData("[笑脸][笑]", true)]
    [InlineData("[笑]你好🙂", false)]
    [InlineData("[笑]123", false)]
    [InlineData("[笑][未知]", false)]
    [InlineData("[笑]!", false)]
    [InlineData("!!!", false)]
    [InlineData("", false)]
    [InlineData(" \n ", false)]
    public void EmoticonOnlyDetectionPreservesNonEmoticonContent(string source, bool expected)
    {
        Assert.Equal(expected, TranslationTextExtractor.IsEmoticonOnly(source, ["[笑]", "[笑脸]", ""]));
    }
}
