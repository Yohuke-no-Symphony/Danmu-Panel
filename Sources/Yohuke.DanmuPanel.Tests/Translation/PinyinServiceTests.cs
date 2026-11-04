using Xunit;
using Yohuke.DanmuPanel.Services.Translation;

namespace Yohuke.DanmuPanel.Tests.Translation;

public sealed class PinyinServiceTests
{
    [Fact]
    public void LocalConversionIncludesToneAndPreservesLatinCharacters()
    {
        var service = new PinyinService();
        var value = service.GetPinyin("小明ABC");
        Assert.Contains("xiǎo", value);
        Assert.Contains("míng", value);
        Assert.Contains("ABC", value);
        Assert.Equal(value, service.GetPinyin("小明ABC"));
        Assert.Equal("", service.GetPinyin("Alice123"));
    }

    [Theory]
    [InlineData("木漏れ日")]
    [InlineData("桜カフェ")]
    [InlineData("山田ｻﾝ")]
    public void NamesWithJapaneseKanaAreNotAnnotatedAsChinese(string name)
    {
        Assert.Equal("", new PinyinService().GetPinyin(name));
    }
}
