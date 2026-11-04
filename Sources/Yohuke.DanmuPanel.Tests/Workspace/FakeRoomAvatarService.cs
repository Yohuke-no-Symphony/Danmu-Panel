using Yohuke.DanmuPanel.Models.Workspace;
using Yohuke.DanmuPanel.Services;

namespace Yohuke.DanmuPanel.Tests.Workspace;

internal sealed class FakeRoomAvatarService : IRoomAvatarService
{
    internal Func<string, string, string?, CancellationToken, Task<RoomAvatarResult>> Resolve { get; init; } =
        (_, _, _, _) => Task.FromResult(new RoomAvatarResult());

    public Task<RoomAvatarResult> GetRoomAvatarResultAsync(string roomId, string ownerUid = "", string? cookie = "",
        CancellationToken cancellationToken = default)
    {
        return Resolve(roomId, ownerUid, cookie, cancellationToken);
    }
}
