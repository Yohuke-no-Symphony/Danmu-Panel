using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Xunit.Abstractions;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证持续批量刷屏期间 UI 输入和消息置顶操作仍能完成。</summary>
public sealed class MessageFeedLoadTests(ITestOutputHelper output)
{
    private const int MAX_BURST_BATCHES = 200;

    /// <summary>后台连续提交消息时，通过真实行按钮置顶和取消置顶，保留持久化状态与一页窗口。</summary>
    [AvaloniaFact]
    public async Task PinAndUnpinCompleteDuringContinuousMessageBursts()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new ConfigurationService(directory.Path), new());
        var room = new RoomTabState { Id = "tab", RoomId = "123" };
        await runtime.TestMessages.AddAsync(room, new()
        {
            Kind = MessageKind.Danmu, UserName = "viewer", Content = "before the burst", Count = 300
        });
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, MessageKind.Danmu, errors.Add);
        var window = new Window { Width = 700, Height = 400, Content = view };
        using var lifetime = new CancellationTokenSource();
        var cancellationToken = lifetime.Token;
        var testMessages = runtime.TestMessages;
        var submitted = 0;
        Task? producer = null;
        try
        {
            await view.Model.SelectRoomAsync(room);
            window.Show();
            Pump(window);
            var target = view.Model.ScrollingItems[^1];
            producer = Task.Run(async () =>
            {
                try
                {
                    for (var batch = 0; batch < MAX_BURST_BATCHES && !cancellationToken.IsCancellationRequested; batch++)
                    {
                        await testMessages.AddAsync(room, new()
                        {
                            Kind = MessageKind.Danmu, UserName = "burst viewer", Content = "during the burst", Count = 100
                        }, cancellationToken);
                        Interlocked.Add(ref submitted, 100);
                        await Task.Delay(25, cancellationToken);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
            });
            var elapsed = Stopwatch.StartNew();
            var input = Dispatcher.UIThread.InvokeAsync(() => FindRow(view, target).RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent)), DispatcherPriority.Input);
            await input;
            await WaitUntilAsync(() => view.Model.PinnedItems.Contains(target));
            Pump(window);
            Assert.True((await runtime.Messages.GetByIdAsync(target.Record.Id))!.IsPinned);

            FindRow(view, target).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => !view.Model.PinnedItems.Contains(target));
            Assert.False((await runtime.Messages.GetByIdAsync(target.Record.Id))!.IsPinned);
            await WaitUntilAsync(() => Volatile.Read(ref submitted) >= 500);
            lifetime.Cancel();
            await producer;
            await view.Model.RefreshAsync();
            Pump(window);

            Assert.Equal(300, view.Model.ScrollingItems.Count);
            Assert.Empty(errors);
            output.WriteLine($"Pin/unpin completed with {submitted} concurrent messages in {elapsed.ElapsedMilliseconds} ms.");
        }
        finally
        {
            lifetime.Cancel();
            try
            {
                if (producer is not null)
                {
                    await producer;
                }
            }
            finally
            {
                view.Model.Dispose();
                window.Close();
            }
        }
    }

    private static Button FindRow(MessageFeedView view, MessageItemViewModel target)
    {
        return view.GetVisualDescendants().OfType<Button>().Single(button => ReferenceEquals(button.DataContext, target));
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(10);
        }
        Assert.Fail("The UI operation did not complete while messages were arriving.");
    }
}
