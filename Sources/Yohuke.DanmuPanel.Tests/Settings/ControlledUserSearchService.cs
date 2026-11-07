using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Services;

namespace Yohuke.DanmuPanel.Tests.Settings;

internal sealed class ControlledUserSearchService : IBilibiliUserSearchService
{
    internal List<(string Query, int Page, string? Cookie, CancellationToken Cancellation)> Requests { get; } = [];
    internal List<TaskCompletionSource<UserSearchPage>> Responses { get; } = [];

    public Task<UserSearchPage> SearchAsync(string keyword, int page = 1, string? cookie = null,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        Requests.Add((keyword, page, cookie, cancellationToken));
        var response = new TaskCompletionSource<UserSearchPage>();
        Responses.Add(response);
        return response.Task;
    }
}
