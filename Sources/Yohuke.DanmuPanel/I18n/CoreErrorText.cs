using Yohuke.DanmuCore.Errors;

namespace Yohuke.DanmuPanel.I18n;

internal static class CoreErrorText
{
    internal static string Get(CoreErrorInfo error)
    {
        var key = error.Code switch
        {
            CoreErrorCode.ApiEmptyJsonResult => "Services.Api.EmptyJsonResult",
            CoreErrorCode.WbiImageKeysMissing => "Services.BilibiliWbi.ImageKeysMissing",
            CoreErrorCode.WebSocketServerDataMissing => "Services.BilibiliWebSocket.ServerDataMissing",
            CoreErrorCode.WebSocketServerMissing => "Services.BilibiliWebSocket.ServerMissing",
            CoreErrorCode.WebSocketConnectionClosed => "Services.BilibiliWebSocket.ConnectionClosed",
            CoreErrorCode.WebSocketHeartbeatStopped => "Services.BilibiliWebSocket.HeartbeatStopped",
            CoreErrorCode.WebSocketServerClosed => "Services.BilibiliWebSocket.ServerClosed",
            CoreErrorCode.WebSocketReceivingStopped => "Services.BilibiliWebSocket.ReceivingStopped",
            CoreErrorCode.WebSocketHeartbeatTimeout => "Services.BilibiliWebSocket.HeartbeatTimeout",
            CoreErrorCode.WebSocketInvalidMessage => "Services.BilibiliWebSocket.InvalidMessage",
            CoreErrorCode.SocketConnectionClosed => "Services.Socket.ConnectionClosed",
            CoreErrorCode.HistoryInvalidRoomId => "Room.InvalidRoomId",
            CoreErrorCode.HistoryRequestFailed => "Room.History.FailedCode",
            CoreErrorCode.RedPocketInvalidRoomId => "Room.InvalidRoomId",
            CoreErrorCode.RedPocketRequestFailed => "Room.RedPocket.FailedCode",
            CoreErrorCode.RoomSearchFollowingLoginRequired => "Services.BilibiliRoomSearch.FollowingLoginRequired",
            CoreErrorCode.RoomSearchInvalidIdentifier => "Services.BilibiliRoomSearch.InvalidIdentifier",
            CoreErrorCode.RoomSearchResultsMissing => "Services.BilibiliRoomSearch.ResultsMissing",
            CoreErrorCode.RoomSearchFailed => "Services.BilibiliRoomSearch.SearchFailed",
            CoreErrorCode.BrowserLoginBilibiliCookiesMissing => "Services.BrowserLogin.BilibiliCookiesMissing",
            _ => null
        };

        if (key is null)
        {
            return error.DiagnosticMessage;
        }

        var arguments = error.Arguments.Select(argument => argument ?? "").ToArray();
        if (error.Code is CoreErrorCode.HistoryRequestFailed or CoreErrorCode.RedPocketRequestFailed)
        {
            var message = Localization.Format(key, arguments.Take(1).ToArray());
            return arguments.Length > 1 ? $"{message} {arguments[1]}".Trim() : message;
        }

        return Localization.Format(key, arguments);
    }
}
