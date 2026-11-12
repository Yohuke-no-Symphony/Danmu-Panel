using System;
using System.Threading;
using System.Threading.Tasks;
using Yohuke.DanmuCore.Apis.Models.Bilibili;
using Yohuke.DanmuPanel.Services;

namespace Yohuke.DanmuPanel.Tests.Integration;

internal sealed class ReceptionTestSocket : ILiveDanmuSocket
{
    public event EventHandler<BilibiliDanmuMessage>? DanmuReceived;
    public event EventHandler<BilibiliGiftMessage>? GiftReceived;
    public event EventHandler<BilibiliSuperChatMessage>? SuperChatReceived;
    public event EventHandler<BilibiliGuardPurchaseMessage>? GuardPurchaseReceived;
    public event EventHandler<BilibiliRedPocketMessage>? RedPocketReceived;
    public event EventHandler<Exception>? ErrorReceived;
    public event EventHandler? Disconnected;
    public event EventHandler? Recovered;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    internal void Emit(BilibiliDanmuMessage message) => DanmuReceived?.Invoke(this, message);
    internal void Emit(BilibiliGiftMessage message) => GiftReceived?.Invoke(this, message);
    internal void Emit(BilibiliSuperChatMessage message) => SuperChatReceived?.Invoke(this, message);
    internal void Emit(BilibiliGuardPurchaseMessage message) => GuardPurchaseReceived?.Invoke(this, message);
    internal void Emit(BilibiliRedPocketMessage message) => RedPocketReceived?.Invoke(this, message);
    internal void Error(Exception exception) => ErrorReceived?.Invoke(this, exception);
    internal void Disconnect() => Disconnected?.Invoke(this, EventArgs.Empty);
    internal void Recover() => Recovered?.Invoke(this, EventArgs.Empty);
}
