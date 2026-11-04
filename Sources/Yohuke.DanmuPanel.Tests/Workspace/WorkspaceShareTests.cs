using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Models.Workspace;
using Yohuke.DanmuPanel.Services;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Workspace;

/// <summary>验证 Neo 分享兼容和本地身份隔离。</summary>
public sealed class WorkspaceShareTests
{
    /// <summary>分享仅输出公共字段，导入重建本地身份。</summary>
    [Fact]
    public async Task ExportContainsOnlyPublicNeoFieldsAndImportUsesFreshIdentities()
    {
        var original = new WorkspaceState
        {
            Name = "直播", AccountOverrideId = "secret-account",
            Rooms = [new() { RoomId = "100", Name = "主播", OwnerUid = "200", AccountOverrideId = "private-account" }]
        };
        var sharing = new WorkspaceShareService();
        await using var stream = new MemoryStream();
        await sharing.ExportAsync(original, stream);
        var json = Encoding.UTF8.GetString(stream.ToArray());
        using var document = JsonDocument.Parse(json);
        Assert.Equal(new[] { "Version", "WorkspaceName", "Rooms" }, document.RootElement.EnumerateObject().Select(value => value.Name));
        Assert.DoesNotContain(original.Id, json);
        Assert.DoesNotContain("account", json);
        stream.Position = 0;
        var imported = await sharing.ImportAsync([original], stream);
        Assert.Equal("直播 (2)", imported.Name);
        Assert.NotEqual(original.Id, imported.Id);
        Assert.NotEqual(original.Rooms[0].Id, imported.Rooms[0].Id);
        Assert.Equal("200", imported.Rooms[0].OwnerUid);
        Assert.Null(imported.AccountOverrideId);
        Assert.Null(imported.Rooms[0].AccountOverrideId);
        Assert.True(imported.IsExpanded);
    }

    /// <summary>读取 Neo v1 时忽略非 Panel 业务字段和身份字段。</summary>
    [Fact]
    public async Task NeoV1ExtraFieldsAreIgnoredWithoutImportingCredentialsOrLyrics()
    {
        const string json = """
            {"Version":1,"WorkspaceName":"Neo","AccountOverrideId":"secret","TranslateOpenMark":"【",
             "Rooms":[{"RoomId":"123","RoomName":"Singer","OwnerUid":"456","Id":"old",
                       "AccountOverrideId":"private","LyricText":"lyrics","TranslatedLyricText":"translation"}]}
            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var imported = await new WorkspaceShareService().ImportAsync([], stream);
        Assert.Equal("Neo", imported.Name);
        Assert.Equal("Singer", imported.Rooms[0].Name);
        Assert.Equal("456", imported.Rooms[0].OwnerUid);
        Assert.NotEqual("old", imported.Rooms[0].Id);
        Assert.Null(imported.Rooms[0].AccountOverrideId);
    }

    /// <summary>无效版本和房间标识不能创建部分工作区。</summary>
    [AvaloniaTheory]
    [InlineData(2, "123", "")]
    [InlineData(1, "invalid", "")]
    [InlineData(1, "0", "")]
    [InlineData(1, "123", "-3")]
    public void InvalidShareCannotCreatePartialWorkspace(int version, string roomId, string ownerUid)
    {
        var package = new WorkspaceSharePackage
        {
            Version = version, WorkspaceName = "Invalid",
            Rooms = [new() { RoomId = roomId, OwnerUid = ownerUid }]
        };
        Assert.Throws<InvalidDataException>(() => new WorkspaceShareService().CreateWorkspace([], package));
    }
}
