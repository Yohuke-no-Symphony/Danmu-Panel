using Newtonsoft.Json.Linq;
using Xunit;
using Yohuke.DanmuPanel.Apis;

namespace Yohuke.DanmuPanel.Tests.Reception;

/// <summary>验证普通弹幕的本直播间身份与佩戴粉丝牌身份分别读取。</summary>
public sealed class BilibiliDanmuIdentityTests
{
    /// <summary>本房间舰队等级来自独立协议字段，不受其他主播的粉丝牌等级影响。</summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 3)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    public void RoomMembershipIsIndependentOfTheWornMedal(int roomGuardLevel, int medalGuardLevel)
    {
        var json = CreateMessage(roomGuardLevel, false, medalGuardLevel);

        var message = BilibiliLiveWebSocket.ParseDanmuMessage("100", json)!;

        Assert.Equal(roomGuardLevel, message.RoomGuardLevel);
        Assert.Equal(medalGuardLevel, message.MedalGuardLevel);
        Assert.False(message.IsRoomAdmin);
    }

    /// <summary>房管标记和本房间舰队身份可以同时存在，缺少粉丝牌也保留本房间身份。</summary>
    [Fact]
    public void RoomAdminAndGuardMembershipSurviveWithoutAMedal()
    {
        var json = CreateMessage(3, true, 0);
        json["info"]![3] = new JArray();

        var message = BilibiliLiveWebSocket.ParseDanmuMessage("100", json)!;

        Assert.True(message.IsRoomAdmin);
        Assert.Equal(3, message.RoomGuardLevel);
        Assert.Empty(message.MedalName);
    }

    /// <summary>未知、缺失或不完整的本房间舰队信息不按粉丝牌身份推断。</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(100)]
    public void UnknownMembershipIsOrdinary(int level)
    {
        var json = CreateMessage(level, false, 1);

        Assert.Equal(0, BilibiliLiveWebSocket.ParseDanmuMessage("100", json)!.RoomGuardLevel);
    }

    /// <summary>旧的简短消息保持普通身份，即使它携带了其他主播的舰队粉丝牌。</summary>
    [Fact]
    public void MissingMembershipDoesNotFallBackToMedalMembership()
    {
        var json = CreateMessage(0, false, 1);
        ((JArray)json["info"]!).RemoveAt(7);

        var message = BilibiliLiveWebSocket.ParseDanmuMessage("100", json)!;

        Assert.Equal(0, message.RoomGuardLevel);
        Assert.Equal(1, message.MedalGuardLevel);
    }

    private static JObject CreateMessage(int roomGuardLevel, bool isRoomAdmin, int medalGuardLevel)
    {
        return new()
        {
            ["cmd"] = "DANMU_MSG",
            ["info"] = new JArray
            {
                new JArray(), "普通弹幕", new JArray(42, "观众", isRoomAdmin ? 1 : 0),
                new JArray(20, "别家牌", "其他主播", 200, 0, "", 0, 0, 0, 0, medalGuardLevel),
                new JArray(), new JArray(), 0, roomGuardLevel
            }
        };
    }
}
