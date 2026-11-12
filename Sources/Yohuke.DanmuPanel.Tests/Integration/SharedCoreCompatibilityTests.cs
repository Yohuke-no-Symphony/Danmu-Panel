using Avalonia.Headless.XUnit;
using System.Text.Json;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证共享核心接入后的产品凭据生命周期与旧消息格式。</summary>
public sealed class SharedCoreCompatibilityTests
{
    /// <summary>清空配置账号撤销凭据，并解除旧产品对象的事件订阅。</summary>
    [AvaloniaFact]
    public async Task ClearingAccountsRevokesCredentialsAndOldObjectsCannotRestoreThem()
    {
        using var directory = new MessageStoreTestDirectory();
        var account = new BilibiliAccount { Cookie = "SESSDATA=session;bili_jct=csrf" };
        await using var runtime = new PanelRuntime(new(directory.Path), new() { Accounts = [account] });
        var credentials = runtime.AccountSessions.GetCredentialCancellationToken(account.Id, account.Cookie);
        Assert.False(credentials.IsCancellationRequested);

        runtime.Configuration.Accounts.Clear();
        account.Cookie = "SESSDATA=old-object;bili_jct=old-csrf";

        Assert.True(credentials.IsCancellationRequested);
        Assert.Equal(AccountValidationStatus.NotBound, runtime.AccountSessions.GetState(account.Id).Status);
        Assert.False(runtime.AccountSessions.CanSend(account.Id, account.Cookie));
    }

    /// <summary>旧红包载荷无需类型名或字段迁移即可恢复并保持同一持久化身份。</summary>
    [Fact]
    public void ExistingRedPocketPayloadKeepsItsIdentityAndUnknownAmounts()
    {
        const string PAYLOAD = """
            {"Id":"red-pocket:tab:100:42","RoomTabId":"tab","RoomId":"100","Kind":4,
             "IsRead":true,"IsPinned":false,"RedPocket":{"LotId":42,"SenderUid":7,
             "SenderName":"viewer","HasDetails":true,"HasResult":true,"TotalPrice":null,
             "Awards":[{"GiftId":"gift","Name":"award","Quantity":2}],
             "Winners":[{"Uid":9,"UserName":"winner","GiftId":"gift","Quantity":1}]}}
            """;
        var restored = Assert.IsType<MessageRecord>(JsonSerializer.Deserialize<MessageRecord>(PAYLOAD));
        var data = Assert.IsType<RedPocketData>(restored.RedPocket);
        var mapped = RoomMessageMapper.Map(new BilibiliRedPocketMessage { RoomId = "100", RedPocket = data }, "tab", 0);

        Assert.Equal(restored.Id, mapped.Id);
        Assert.Equal(MessageKind.RedPocket, restored.Kind);
        Assert.True(restored.IsRead);
        Assert.Null(data.TotalPrice);
        Assert.Equal("award", Assert.Single(data.Awards).Name);
        Assert.Equal("winner", Assert.Single(data.Winners).UserName);
        Assert.DoesNotContain("IsDrawn", JsonSerializer.Serialize(restored));
    }
}
