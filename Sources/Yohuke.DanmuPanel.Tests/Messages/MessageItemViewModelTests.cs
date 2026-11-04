using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.ViewModels;

namespace Yohuke.DanmuPanel.Tests.Messages;

/// <summary>验证消息行的粉丝牌名称显示边界。</summary>
public sealed class MessageItemViewModelTests
{
    /// <summary>空名称可以直接读取，长名称最多显示三个完整文本元素。</summary>
    [Theory]
    [InlineData("", "")]
    [InlineData("粉", "粉")]
    [InlineData("粉丝", "粉丝")]
    [InlineData("粉丝牌", "粉丝牌")]
    [InlineData("粉丝牌名", "粉丝牌")]
    [InlineData("😀😃😄😁", "😀😃😄")]
    [InlineData("e\u0301a\u0308o\u0302u\u0308", "e\u0301a\u0308o\u0302")]
    [InlineData("👨‍👩‍👧‍👦粉丝牌", "👨‍👩‍👧‍👦粉丝")]
    public void MedalNamePreservesUpToThreeTextElements(string name, string expected)
    {
        var record = new MessageRecord { MedalName = name };
        var model = new MessageItemViewModel(record, new(), new PinyinService());

        Assert.Equal(expected, model.MedalName);
        Assert.Equal(name, model.Record.MedalName);
    }

    /// <summary>消息更新为无粉丝牌状态后，名称可读取且徽章隐藏。</summary>
    [Fact]
    public void RemovingMedalClearsDisplayedName()
    {
        var record = new MessageRecord { MedalName = "粉丝牌名", MedalLevel = 20 };
        var settings = new PanelSettings();
        var model = new MessageItemViewModel(record, settings, new PinyinService());
        Assert.Equal("粉丝牌", model.MedalName);
        Assert.True(model.HasMedal);

        model.Update(record with { MedalName = "", MedalLevel = 0 }, settings);

        Assert.Equal("", model.MedalName);
        Assert.False(model.HasMedal);
    }
}
