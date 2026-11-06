using System.Collections.Specialized;
using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Xunit;
using Xunit.Abstractions;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.ViewModels;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证单条弹幕置顶移组不会逐条搬动未变化的历史。</summary>
public sealed class PinReorderTests(ITestOutputHelper output)
{
    /// <summary>八百条历史中的置顶及取消置顶只移动目标投影，保留其他消息的顺序与实例。</summary>
    [AvaloniaTheory]
    [InlineData(400)]
    [InlineData(799)]
    public async Task PinAndUnpinMoveOnlyTheTargetMessage(int targetIndex)
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = new PanelConfiguration();
        configuration.Settings.MessageRetentionCount = 800;
        configuration.Settings.Translation.Enabled = false;
        await using var runtime = new PanelRuntime(new(directory.Path), configuration);
        var room = new RoomTabState { Id = "tab", RoomId = "123" };
        for (var batch = 0; batch < 2; batch++)
        {
            await runtime.TestMessages.AddAsync(room, new()
            {
                Kind = MessageKind.Danmu, UserName = "viewer", Content = "历史消息", Count = 400
            });
        }
        using var model = new FeedViewModel(runtime, MessageKind.Danmu);
        await model.SelectRoomAsync(room);
        await model.WaitForPresentationAsync();
        var originals = model.Items.ToArray();
        var target = originals[targetIndex];
        var moves = new List<MessageItemViewModel>();
        model.Items.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Move)
            {
                moves.Add(Assert.IsType<MessageItemViewModel>(args.OldItems![0]));
            }
        };

        var startedAt = Stopwatch.GetTimestamp();
        await runtime.Messages.TogglePinAsync(target.Record.Id);
        await model.RefreshAsync();
        await model.WaitForPresentationAsync();
        output.WriteLine($"Pin at {targetIndex}: {moves.Count} moves, {Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds:F1} ms.");
        Assert.Same(target, Assert.Single(moves));
        Assert.Same(target, Assert.Single(model.PinnedItems));
        Assert.Equal(originals.Where(item => !ReferenceEquals(item, target)), model.ScrollingItems);

        moves.Clear();
        startedAt = Stopwatch.GetTimestamp();
        await runtime.Messages.TogglePinAsync(target.Record.Id);
        await model.RefreshAsync();
        await model.WaitForPresentationAsync();
        output.WriteLine($"Unpin at {targetIndex}: {moves.Count} moves, {Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds:F1} ms.");

        Assert.Same(target, Assert.Single(moves));
        Assert.Equal(originals, model.Items);
        Assert.Equal(originals, model.ScrollingItems);
        Assert.Equal(originals, model.ScrollingRows);
        Assert.Empty(model.PinnedItems);
        Assert.False(target.Record.IsPinned);
        Assert.False((await runtime.Messages.GetByIdAsync(target.Record.Id))!.IsPinned);
        Assert.Equal(0, model.ViewportUpdateDepth);
    }
}
