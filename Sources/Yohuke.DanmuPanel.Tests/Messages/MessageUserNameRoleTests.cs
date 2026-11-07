using System.Text.Json;
using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services.Messages;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.ViewModels;

namespace Yohuke.DanmuPanel.Tests.Messages;

/// <summary>验证用户名身份的优先级、变更通知和持久化兼容性。</summary>
public sealed class MessageUserNameRoleTests
{
    /// <summary>特别关注、房管、舰队与普通用户按约定的优先级选择用户名身份。</summary>
    [Theory]
    [InlineData(0, false, false, MessageUserNameRole.Regular)]
    [InlineData(1, false, false, MessageUserNameRole.Governor)]
    [InlineData(2, false, false, MessageUserNameRole.Admiral)]
    [InlineData(3, false, false, MessageUserNameRole.Captain)]
    [InlineData(0, true, false, MessageUserNameRole.RoomAdmin)]
    [InlineData(1, true, false, MessageUserNameRole.RoomAdmin)]
    [InlineData(3, true, true, MessageUserNameRole.SpecialAttention)]
    [InlineData(2, false, true, MessageUserNameRole.SpecialAttention)]
    [InlineData(4, false, false, MessageUserNameRole.Regular)]
    public void UserNameRoleUsesRoomIdentityAndAttentionPriority(int level, bool isRoomAdmin, bool followed,
        MessageUserNameRole expected)
    {
        var settings = new PanelSettings();
        if (followed)
        {
            settings.SpecialAttention.Users.Add(new() { UserUid = 42 });
        }
        var record = new MessageRecord
        {
            Kind = MessageKind.Danmu, UserUid = 42, RoomGuardLevel = level, IsRoomAdmin = isRoomAdmin,
            MedalGuardLevel = 1
        };

        Assert.Equal(expected, new MessageItemViewModel(record, settings, new PinyinService()).UserNameRole);
    }

    /// <summary>其他消息面板不套用普通弹幕的名称身份着色。</summary>
    [Theory]
    [InlineData(MessageKind.SuperChat)]
    [InlineData(MessageKind.Gift)]
    [InlineData(MessageKind.Guard)]
    public void OtherMessageKindsKeepTheirRegularNameRole(MessageKind kind)
    {
        var settings = new PanelSettings();
        settings.SpecialAttention.Users.Add(new() { UserUid = 42 });
        var record = new MessageRecord { Kind = kind, UserUid = 42, RoomGuardLevel = 3, IsRoomAdmin = true, GuardLevel = 3 };

        Assert.Equal(MessageUserNameRole.Regular,
            new MessageItemViewModel(record, settings, new PinyinService()).UserNameRole);
    }

    /// <summary>原位身份更新只通知相关显示属性，不重绑名称内容或弹幕正文。</summary>
    [Fact]
    public void IdentityAndAttentionChangesNotifyTheNameRole()
    {
        var settings = new PanelSettings();
        var record = new MessageRecord { UserUid = 42, UserName = "观众", Content = "正文", RoomGuardLevel = 3 };
        var model = new MessageItemViewModel(record, settings, new PinyinService());
        var changes = new List<string?>();
        model.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        record = record with { IsRoomAdmin = true };
        model.Update(record, settings);
        Assert.Equal(MessageUserNameRole.RoomAdmin, model.UserNameRole);
        Assert.Contains(nameof(MessageItemViewModel.UserNameRole), changes);
        Assert.DoesNotContain(nameof(MessageItemViewModel.Name), changes);
        Assert.DoesNotContain(nameof(MessageItemViewModel.Detail), changes);

        changes.Clear();
        settings.SpecialAttention.Users.Add(new() { UserUid = 42 });
        model.Update(record, settings);
        Assert.Equal(MessageUserNameRole.SpecialAttention, model.UserNameRole);
        Assert.Contains(nameof(MessageItemViewModel.UserNameRole), changes);
        settings.SpecialAttention.Users.Clear();
        model.Update(record, settings);
        Assert.Equal(MessageUserNameRole.RoomAdmin, model.UserNameRole);
    }

    /// <summary>本地历史保留本房间身份，旧记录缺失新字段时继续按普通用户显示。</summary>
    [Fact]
    public async Task RoomIdentitySurvivesStorageAndOldRecordsRemainOrdinary()
    {
        using var directory = new MessageStoreTestDirectory();
        var record = new MessageRecord { RoomTabId = "room", RoomGuardLevel = 2, IsRoomAdmin = true, MedalGuardLevel = 3 };
        await using (var writer = new SqliteMessageStore(directory.DatabasePath))
        {
            await writer.AppendAsync(record, new());
        }
        await using var reopened = new SqliteMessageStore(directory.DatabasePath);
        var restored = Assert.Single(await reopened.LoadPageAsync("room", MessageKind.Danmu, 0, 10));
        Assert.Equal(2, restored.RoomGuardLevel);
        Assert.True(restored.IsRoomAdmin);
        Assert.Equal(3, restored.MedalGuardLevel);

        var legacy = JsonSerializer.Deserialize<MessageRecord>("""{"MedalGuardLevel":1,"UserName":"旧消息"}""")!;
        Assert.False(legacy.IsRoomAdmin);
        Assert.Equal(0, legacy.RoomGuardLevel);
        Assert.Equal(MessageUserNameRole.Regular,
            new MessageItemViewModel(legacy, new(), new PinyinService()).UserNameRole);
    }
}
