using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.ViewModels;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Messages;

/// <summary>验证特别关注名单在消息呈现快照中的身份比较及可变配置隔离。</summary>
public sealed class SpecialAttentionProjectionTests
{
    /// <summary>名单原地添加和删除时创建新快照，顺序与名称编辑不影响消息呈现。</summary>
    [Fact]
    public void SnapshotTracksUidMembershipAndIgnoresOrderAndNames()
    {
        var settings = new PanelSettings
        {
            SpecialAttention = new() { Users = [new() { UserUid = 42, DisplayName = "first" }, new() { UserUid = 73 }] }
        };
        var original = MessageDisplaySettings.Capture(settings);
        settings.SpecialAttention.Users.Reverse();
        settings.SpecialAttention.Users[0].DisplayName = "renamed";
        Assert.Same(original, MessageDisplaySettings.Capture(settings, original));

        settings.SpecialAttention.Users.Add(new() { UserUid = 99 });
        var added = MessageDisplaySettings.Capture(settings, original);
        Assert.NotSame(original, added);
        Assert.Same(added, MessageDisplaySettings.Capture(settings, added));

        settings.SpecialAttention.Users.RemoveAt(0);
        var removed = MessageDisplaySettings.Capture(settings, added);
        Assert.NotSame(added, removed);
        Assert.Same(removed, MessageDisplaySettings.Capture(settings, removed));

        settings.SpecialAttention.Users[0].UserUid = long.MaxValue;
        var replaced = MessageDisplaySettings.Capture(settings, removed);
        Assert.NotSame(removed, replaced);
        Assert.True(removed.IsSpecialAttention(42));
        Assert.False(replaced.IsSpecialAttention(42));
        Assert.True(replaced.IsSpecialAttention(long.MaxValue));
        Assert.False(original.IsSpecialAttention(99));
        Assert.True(added.IsSpecialAttention(99));
    }

    /// <summary>同一可变配置中的关注变更仅通知强调状态，不重算正文、译文或拼音。</summary>
    [Fact]
    public void InPlaceMembershipChangesNotifyOnlyTheAttentionState()
    {
        var settings = new PanelSettings { ShowDanmuPinyin = true };
        var record = new MessageRecord { Kind = MessageKind.Danmu, UserUid = 42, UserName = "用户", Content = "你好" };
        var model = new MessageItemViewModel(record, settings, new PinyinService());
        var annotations = model.ContentPinyin;
        var changed = new List<string?>();
        model.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        Assert.False(model.IsSpecialAttention);

        settings.SpecialAttention.Users.Add(new() { UserUid = 42 });
        model.Update(record, settings);

        Assert.True(model.IsSpecialAttention);
        Assert.Equal(new[] { nameof(MessageItemViewModel.IsSpecialAttention) }, changed);
        Assert.Same(annotations, model.ContentPinyin);
        changed.Clear();
        settings.SpecialAttention.Users[0].DisplayName = "renamed";
        model.Update(record, settings);
        Assert.Empty(changed);

        settings.SpecialAttention.Users.Clear();
        model.Update(record, settings);

        Assert.False(model.IsSpecialAttention);
        Assert.Equal(new[] { nameof(MessageItemViewModel.IsSpecialAttention) }, changed);
        Assert.Same(annotations, model.ContentPinyin);
    }

    /// <summary>仅正数 UID 命中的普通弹幕具有强调状态。</summary>
    [Theory]
    [InlineData(MessageKind.Danmu, 42, true)]
    [InlineData(MessageKind.Danmu, 73, false)]
    [InlineData(MessageKind.Danmu, 0, false)]
    [InlineData(MessageKind.Danmu, -1, false)]
    [InlineData(MessageKind.SuperChat, 42, false)]
    [InlineData(MessageKind.Gift, 42, false)]
    [InlineData(MessageKind.Guard, 42, false)]
    public void AttentionAppliesOnlyToMatchingDanmu(MessageKind kind, long userUid, bool expected)
    {
        var settings = new PanelSettings { SpecialAttention = new() { Users = [new() { UserUid = 42 }] } };
        var model = new MessageItemViewModel(new() { Kind = kind, UserUid = userUid }, settings, new PinyinService());

        Assert.Equal(expected, model.IsSpecialAttention);
    }

    /// <summary>记录换成其他 UID 或消息类型时立即重算关注投影。</summary>
    [Fact]
    public void RecordIdentityAndKindChangesUpdateTheAttentionState()
    {
        var settings = new PanelSettings { SpecialAttention = new() { Users = [new() { UserUid = 42 }] } };
        var record = new MessageRecord { Kind = MessageKind.Danmu, UserUid = 42 };
        var model = new MessageItemViewModel(record, settings, new PinyinService());
        Assert.True(model.IsSpecialAttention);

        model.Update(record with { UserUid = 73 }, settings);
        Assert.False(model.IsSpecialAttention);
        model.Update(record, settings);
        Assert.True(model.IsSpecialAttention);
        model.Update(record with { Kind = MessageKind.SuperChat }, settings);
        Assert.False(model.IsSpecialAttention);
    }
}
