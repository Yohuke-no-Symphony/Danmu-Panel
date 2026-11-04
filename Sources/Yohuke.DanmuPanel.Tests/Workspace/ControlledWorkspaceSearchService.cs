using Yohuke.DanmuPanel.Models.Workspace;
using Yohuke.DanmuPanel.Services;

namespace Yohuke.DanmuPanel.Tests.Workspace;

internal sealed class ControlledWorkspaceSearchService : IBilibiliLiveRoomSearchService
{
    internal List<(string Query, int Page, CancellationToken Cancellation)> Requests { get; } = [];
    internal List<TaskCompletionSource<LiveRoomSearchPage>> Responses { get; } = [];

    public Task<LiveRoomSearchPage> SearchAsync(string keyword, int page = 1, string? cookie = null,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        Requests.Add((keyword, page, cancellationToken));
        var response = new TaskCompletionSource<LiveRoomSearchPage>();
        Responses.Add(response);
        return response.Task;
    }
}
