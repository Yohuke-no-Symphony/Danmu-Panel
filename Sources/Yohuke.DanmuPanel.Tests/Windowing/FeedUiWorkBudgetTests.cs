using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yohuke.DanmuPanel.ViewModels;

namespace Yohuke.DanmuPanel.Tests.Windowing;

/// <summary>验证消息 UI 分批让出优先级时不强制等待额外帧，并保留输入优先和取消语义。</summary>
public sealed class FeedUiWorkBudgetTests
{
    /// <summary>一批便宜更新让输入和后台队列执行后即可继续，无需等待新的计时器帧。</summary>
    [AvaloniaFact]
    public async Task ReadyUiWorkResumesAfterQueuedInputWithoutWaitingForATimer()
    {
        var budget = new FeedUiWorkBudget();
        for (var index = 0; index < 19; index++)
        {
            await budget.YieldIfNeededAsync(CancellationToken.None);
        }
        var pending = budget.YieldIfNeededAsync(CancellationToken.None);
        var inputHandled = false;
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => inputHandled = true, DispatcherPriority.Input);
            await Dispatcher.UIThread.InvokeAsync(static () =>
            {
            }, DispatcherPriority.Background);

            Assert.True(inputHandled);
            Assert.True(pending.IsCompleted, "Ready UI work must resume after pending input and background work, without a frame timer.");
        }
        finally
        {
            await pending;
        }
    }

    /// <summary>已经取消的工作不再提交 UI 继续执行的请求。</summary>
    [AvaloniaFact]
    public void CanceledWorkCannotStartAnotherBatch()
    {
        using var lifetime = new CancellationTokenSource();
        lifetime.Cancel();
        var budget = new FeedUiWorkBudget();

        Assert.Throws<OperationCanceledException>(() =>
        {
            _ = budget.YieldIfNeededAsync(lifetime.Token);
        });
    }
}
