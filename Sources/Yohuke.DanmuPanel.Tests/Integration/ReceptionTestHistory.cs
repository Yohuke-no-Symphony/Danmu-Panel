using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Yohuke.DanmuCore.Apis.Models.Bilibili;
using Yohuke.DanmuPanel.Services;

namespace Yohuke.DanmuPanel.Tests.Integration;

internal sealed class ReceptionTestHistory : IBilibiliDanmuHistoryService
{
    internal IReadOnlyList<BilibiliDanmuMessage> Messages { get; init; } = [];
    public Task<IReadOnlyList<BilibiliDanmuMessage>> GetRecentAsync(string roomId, string cookie,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Messages);
    }
}
