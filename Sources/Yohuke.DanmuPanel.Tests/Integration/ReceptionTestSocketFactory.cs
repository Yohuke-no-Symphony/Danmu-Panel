using Yohuke.DanmuPanel.Services;

namespace Yohuke.DanmuPanel.Tests.Integration;

internal sealed class ReceptionTestSocketFactory(ReceptionTestSocket socket) : ILiveDanmuSocketFactory
{
    public ILiveDanmuSocket Create(long roomId, string cookie) => socket;
}
