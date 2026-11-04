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
}
