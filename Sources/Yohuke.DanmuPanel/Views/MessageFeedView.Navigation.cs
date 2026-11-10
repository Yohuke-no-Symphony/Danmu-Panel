using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.ViewModels;

namespace Yohuke.DanmuPanel.Views;

public sealed partial class MessageFeedView
{
    private static readonly TimeSpan PING_DURATION = TimeSpan.FromMilliseconds(1400);
    private CancellationTokenSource? pingRequest;
    private IDisposable? pingTimer;
    private MessageItemViewModel? pingedItem;

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && !IsVisible)
        {
            CancelPing();
        }
    }

    /// <summary>定位当前房间中仍然存在的付费消息，选择其阅读标签并播放动态提示。</summary>
    public async Task PingAsync(MessageRecord record)
    {
        CancelPing();
        var request = new CancellationTokenSource();
        var token = request.Token;
        pingRequest = request;
        try
        {
            var item = await Model.FindMessageAsync(record, token);
            token.ThrowIfCancellationRequested();
            if (item is null)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                token.ThrowIfCancellationRequested();
                if (this.GetVisualRoot() is null || !IsEffectivelyVisible || !Model.Items.Contains(item) ||
                    Model.SelectedRoom?.Id != record.RoomTabId)
                {
                    return;
                }

                isScrollToTopPending = false;
                UpdateLayout();
                if (scroll is not null)
                {
                    scroll.ScrollToItem(item);
                }
                else
                {
                    list.ScrollIntoView(item);
                    UpdateLayout();
                }
                Model.IsReadingHistory = true;
                pingedItem = item;
                item.IsPinged = true;
                pingTimer = DispatcherTimer.RunOnce(() =>
                {
                    if (ReferenceEquals(pingRequest, request))
                    {
                        CancelPing();
                    }
                }, PING_DURATION);
            }, DispatcherPriority.Loaded);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(pingRequest, request) && pingedItem is null)
            {
                CancelPing();
            }
        }
    }

    private void CancelPing()
    {
        pingTimer?.Dispose();
        pingTimer = null;
        pingRequest?.Cancel();
        pingRequest?.Dispose();
        pingRequest = null;
        if (pingedItem is not null)
        {
            pingedItem.IsPinged = false;
            pingedItem = null;
        }
    }
}
