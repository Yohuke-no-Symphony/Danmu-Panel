using Xunit;
using Xunit.Abstractions;
using System.Text.Json;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.ViewModels;

namespace Yohuke.DanmuPanel.Tests.Messages;

/// <summary>验证消息行的粉丝牌名称显示边界。</summary>
public sealed class MessageItemViewModelTests(ITestOutputHelper output)
{
    /// <summary>数据库反序列化产生的新记录及表情集合不应使未变化的消息行重绑。</summary>
    [Fact]
    public void EquivalentDatabaseSnapshotDoesNotNotifyOrReplaceBrushes()
    {
        var settings = new PanelSettings();
        var record = new MessageRecord
        {
            UserName = "viewer", Content = "unchanged", MedalGuardLevel = 2,
            Emoticons = [new() { Text = "[smile]", Url = "https://example.invalid/smile.png" }]
        };
        var model = new MessageItemViewModel(record, settings, new PinyinService());
        var border = model.BorderBrush;
        var medalBorder = model.MedalBorderBrush;
        var changes = new List<string?>();
        model.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        var restored = JsonSerializer.Deserialize<MessageRecord>(JsonSerializer.Serialize(record))!;

        model.Update(restored, settings);

        Assert.Empty(changes);
        Assert.Same(restored, model.Record);
        Assert.Same(border, model.BorderBrush);
        Assert.Same(medalBorder, model.MedalBorderBrush);
    }

    /// <summary>置顶只更新徽章与边框，不重绑原文、译文或图片。</summary>
    [Fact]
    public void PinChangeDoesNotNotifyMessageContent()
    {
        var settings = new PanelSettings();
        var record = new MessageRecord { UserName = "viewer", Content = "unchanged" };
        var model = new MessageItemViewModel(record, settings, new PinyinService());
        var changes = new List<string?>();
        model.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        model.Update(record with { IsPinned = true, IsPriority = true }, settings);

        Assert.Contains(nameof(MessageItemViewModel.ShowPinBadge), changes);
        Assert.Contains(nameof(MessageItemViewModel.BorderBrush), changes);
        Assert.DoesNotContain(string.Empty, changes);
        Assert.DoesNotContain(nameof(MessageItemViewModel.Detail), changes);
        Assert.DoesNotContain(nameof(MessageItemViewModel.Translation), changes);
        Assert.DoesNotContain(nameof(MessageItemViewModel.Emoticons), changes);
    }

    /// <summary>绑定反复读取译文与显隐状态时复用已计算的显示值。</summary>
    [Fact]
    public void TranslationGettersHaveBoundedAllocations()
    {
        var settings = new PanelSettings();
        var record = new MessageRecord
        {
            Translation = "translated",
            TranslationFingerprint = TranslationRequestFactory.GetConfigurationFingerprint(settings.Translation)
        };
        var model = new MessageItemViewModel(record, settings, new PinyinService());
        Assert.True(model.HasTranslation);
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var index = 0; index < 1000; index++)
        {
            _ = model.Translation;
            _ = model.HasTranslation;
            _ = model.ShowOriginal;
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"1000 translation getter groups allocated {allocated} bytes.");
        Assert.True(allocated < 1024, $"Repeated translation reads allocated {allocated} bytes.");
    }

    /// <summary>同一个可变设置对象被编辑后，显示快照仍能识别拼音与颜色变化。</summary>
    [Fact]
    public void InPlaceSettingsChangesUpdateOnlyAffectedPresentation()
    {
        var settings = new PanelSettings();
        var record = new MessageRecord { UserName = "观众", Content = "unchanged", MedalGuardLevel = 2 };
        var model = new MessageItemViewModel(record, settings, new PinyinService());
        var changes = new List<string?>();
        model.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        var medalBorder = model.MedalBorderBrush;
        settings.ShowUserPinyin = true;

        model.Update(record, settings);

        Assert.True(model.HasNamePinyin);
        Assert.Same(medalBorder, model.MedalBorderBrush);
        Assert.DoesNotContain(nameof(MessageItemViewModel.MedalBorderBrush), changes);
        settings.ColorScheme.Overrides["GuardAdmiralBrush"] = "#123456";
        model.Update(record, settings);
        Assert.Contains(nameof(MessageItemViewModel.NamePinyin), changes);
        Assert.Contains(nameof(MessageItemViewModel.MedalBorderBrush), changes);
        Assert.DoesNotContain(nameof(MessageItemViewModel.Detail), changes);
    }

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
