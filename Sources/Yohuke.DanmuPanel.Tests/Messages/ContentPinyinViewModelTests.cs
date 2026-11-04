using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.ViewModels;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Messages;

/// <summary>验证正文注音的独立开关、增量更新和显示模式。</summary>
public sealed class ContentPinyinViewModelTests
{
    /// <summary>正文开关只作用于相应消息类别。</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ContentSwitchesAreIndependent(bool danmuEnabled, bool superChatEnabled)
    {
        var settings = new PanelSettings { ShowDanmuPinyin = danmuEnabled, ShowSuperChatPinyin = superChatEnabled };
        foreach (var kind in Enum.GetValues<MessageKind>())
        {
            var record = new MessageRecord { Kind = kind, Content = "你好" };
            var model = new MessageItemViewModel(record, settings, new PinyinService());
            var expected = kind == MessageKind.Danmu && danmuEnabled || kind == MessageKind.SuperChat && superChatEnabled;

            Assert.Equal(expected, model.HasContentPinyin);
            Assert.Equal(expected, model.ContentPinyin.Count > 0);
        }
    }

    /// <summary>只更改布局、置顶或已读状态时不重复转换正文。</summary>
    [Fact]
    public void PresentationAndReadChangesReuseAnnotations()
    {
        var settings = new PanelSettings { ShowDanmuPinyin = true };
        var record = new MessageRecord { Kind = MessageKind.Danmu, Content = "你好" };
        var service = new CountingPinyinService();
        var model = new MessageItemViewModel(record, settings, service);
        var annotations = model.ContentPinyin;
        var changes = new List<string?>();
        model.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        settings.PinyinLayout = PinyinLayoutMode.SeparateLine;
        model.Update(record with { IsPinned = true, IsRead = true }, settings);

        Assert.Same(annotations, model.ContentPinyin);
        Assert.Equal(1, service.AnnotationCalls);
        Assert.Equal(PinyinLayoutMode.SeparateLine, model.PinyinLayout);
        Assert.Contains(nameof(MessageItemViewModel.PinyinLayout), changes);
        Assert.DoesNotContain(nameof(MessageItemViewModel.ContentPinyin), changes);
    }

    /// <summary>原地切换配置、修改正文及变更类别均即时更新。</summary>
    [Fact]
    public void ContentAndInPlaceSwitchChangesRefreshTheProjection()
    {
        var settings = new PanelSettings();
        var record = new MessageRecord { Kind = MessageKind.Danmu, Content = "你好" };
        var service = new CountingPinyinService();
        var model = new MessageItemViewModel(record, settings, service);
        Assert.Empty(model.ContentPinyin);
        Assert.Equal(0, service.AnnotationCalls);

        settings.ShowDanmuPinyin = true;
        model.Update(record, settings);
        Assert.True(model.HasContentPinyin);
        Assert.Equal(1, service.AnnotationCalls);

        record = record with { Content = "世界" };
        model.Update(record, settings);
        Assert.Equal("世界", string.Concat(model.ContentPinyin.Select(annotation => annotation.Text)));
        Assert.Equal(2, service.AnnotationCalls);

        record = record with { Kind = MessageKind.SuperChat };
        model.Update(record, settings);
        Assert.False(model.HasContentPinyin);
        settings.ShowSuperChatPinyin = true;
        model.Update(record, settings);
        Assert.True(model.HasContentPinyin);
        Assert.Equal(3, service.AnnotationCalls);

        settings.ShowSuperChatPinyin = false;
        model.Update(record, settings);
        Assert.Empty(model.ContentPinyin);
        Assert.False(model.HasContentPinyin);
    }

    /// <summary>纯英文没有注音，而仅译文模式仍服从原文显隐规则。</summary>
    [Fact]
    public void UnsupportedContentAndTranslationVisibilityRemainConsistent()
    {
        var settings = new PanelSettings { ShowDanmuPinyin = true };
        var record = new MessageRecord { Kind = MessageKind.Danmu, Content = "Alice" };
        var model = new MessageItemViewModel(record, settings, new PinyinService());
        Assert.False(model.HasContentPinyin);

        settings.Translation.DisplayMode = TranslationDisplayMode.TranslationOnly;
        record = record with
        {
            Content = "你好", Translation = "Hello",
            TranslationFingerprint = TranslationRequestFactory.GetConfigurationFingerprint(settings.Translation)
        };
        model.Update(record, settings);
        Assert.True(model.HasContentPinyin);
        Assert.True(model.HasTranslation);
        Assert.False(model.ShowOriginal);
    }

    /// <summary>用户名与礼物名复用正文的逐字注音，礼物金额数量更新后映射仍覆盖完整显示文本。</summary>
    [Fact]
    public void UserAndGiftNamesShareAnnotationLayoutWithoutAnnotatingThePrice()
    {
        var settings = new PanelSettings { ShowUserPinyin = true, ShowGiftPinyin = true };
        var record = new MessageRecord
        {
            Kind = MessageKind.Gift, UserName = "小明ABC", GiftName = "鲜花", Quantity = 1, TotalPrice = 10
        };
        var model = new MessageItemViewModel(record, settings, new PinyinService());
        Assert.Equal(record.UserName, string.Concat(model.NameAnnotations.Select(annotation => annotation.Text)));
        Assert.Equal(model.Detail, string.Concat(model.ContentPinyin.Select(annotation => annotation.Text)));
        Assert.Equal("鲜花", string.Concat(model.ContentPinyin.Where(annotation => annotation.HasPinyin)
            .Select(annotation => annotation.Text)));
        var nameAnnotations = model.NameAnnotations;
        var giftAnnotations = model.ContentPinyin;

        settings.PinyinLayout = PinyinLayoutMode.SeparateLine;
        model.Update(record, settings);
        Assert.Same(nameAnnotations, model.NameAnnotations);
        Assert.Same(giftAnnotations, model.ContentPinyin);
        Assert.Equal(PinyinLayoutMode.SeparateLine, model.PinyinLayout);

        record = record with { Quantity = 2, TotalPrice = 20 };
        model.Update(record, settings);
        Assert.Same(nameAnnotations, model.NameAnnotations);
        Assert.NotSame(giftAnnotations, model.ContentPinyin);
        Assert.Equal(model.Detail, string.Concat(model.ContentPinyin.Select(annotation => annotation.Text)));

        settings.ShowUserPinyin = false;
        model.Update(record, settings);
        Assert.Empty(model.NameAnnotations);
        Assert.NotEmpty(model.ContentPinyin);
        settings.ShowGiftPinyin = false;
        model.Update(record, settings);
        Assert.Empty(model.ContentPinyin);
    }

    /// <summary>姓名与礼物的假名排除策略保持一致，避免已有日语名称被标为中文。</summary>
    [Fact]
    public void JapaneseNamesAndGiftNamesKeepTheExistingExclusionPolicy()
    {
        var model = new MessageItemViewModel(new()
        {
            Kind = MessageKind.Gift, UserName = "木漏れ日", GiftName = "桜カフェ"
        }, new() { ShowUserPinyin = true, ShowGiftPinyin = true }, new PinyinService());

        Assert.False(model.HasNamePinyin);
        Assert.Empty(model.NameAnnotations);
        Assert.False(model.HasDetailPinyin);
        Assert.Empty(model.ContentPinyin);
    }
}
