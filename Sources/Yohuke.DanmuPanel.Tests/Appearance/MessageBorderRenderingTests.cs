using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Tests.Appearance;

/// <summary>验证真实消息与加载骨架共用可动态更新的边框粗细。</summary>
public sealed class MessageBorderRenderingTests
{
    /// <summary>两种布局中的已有卡片和骨架都按类别读取粗细，并同步更新至范围边界。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.SuperChat, false, 1d)]
    [InlineData(MessageKind.SuperChat, true, 1d)]
    [InlineData(MessageKind.Gift, false, 2.5d)]
    [InlineData(MessageKind.Gift, true, 2.5d)]
    [InlineData(MessageKind.Guard, false, 4d)]
    [InlineData(MessageKind.Guard, true, 4d)]
    public async Task ExistingCardsAndSkeletonsRefreshPerKindThickness(MessageKind kind, bool compact, double initial)
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = new PanelConfiguration
        {
            Settings = new()
            {
                CompactDanmuDisplay = compact,
                MessageBorders = new() { SuperChat = 1, Gift = 2.5, Guard = 4 }
            }
        };
        await using var runtime = new PanelRuntime(new(directory.Path), configuration);
        var room = new RoomTabState { Id = "room", RoomId = "123" };
        await runtime.Messages.AppendAsync(new()
        {
            RoomTabId = room.Id, RoomId = room.RoomId, Kind = kind, UserName = "viewer",
            Content = "message", GiftName = "gift", GuardLevel = 3, UnitPrice = 2, TotalPrice = 2
        }, new());
        var view = new MessageFeedView(runtime, kind, exception => throw exception);
        var window = new Window { Content = view, Width = 760, Height = 420 };
        try
        {
            await view.Model.SelectRoomAsync(room);
            window.Show();
            Pump(window);
            var item = Assert.Single(view.Model.Items);
            var list = view.GetVisualDescendants().OfType<ListBox>().Single(control => control.Name == "ScrollingMessages");
            var placeholder = new MessageLoadingPlaceholderViewModel();
            list.ItemsSource = new ObservableCollection<FeedRowViewModel> { item, placeholder };
            Pump(window);
            var row = Assert.IsType<MessageFeedListBoxItem>(list.ContainerFromItem(item)).Row;
            var skeleton = Assert.IsType<MessageLoadingListBoxItem>(list.ContainerFromItem(placeholder)).Skeleton;
            var frame = FindFrame(row);
            var skeletonFrame = FindFrame(skeleton);
            Assert.Equal(new Thickness(initial), frame.BorderThickness);
            Assert.Equal(frame.BorderThickness, skeletonFrame.BorderThickness);
            foreach (var thickness in new[] { 0.5, 10d })
            {
                SetThickness(configuration.Settings, kind, thickness);
                runtime.NotifyAppearanceChanged();
                Pump(window);
                Assert.Same(frame, FindFrame(row));
                Assert.Equal(new Thickness(thickness), frame.BorderThickness);
                Assert.Equal(frame.BorderThickness, skeletonFrame.BorderThickness);
                Assert.True(frame.Child!.Bounds.Width > 0);
                Assert.True(frame.Child.Bounds.Height > 0);
            }
            if (kind == MessageKind.Gift && !compact)
            {
                window.Content = null;
                SetThickness(configuration.Settings, kind, 3.5);
                runtime.NotifyAppearanceChanged();
                window.Content = view;
                Pump(window);
                var reattachedFrame = FindFrame(Assert.IsType<MessageFeedListBoxItem>(list.ContainerFromItem(item)).Row);
                var reattachedSkeleton = FindFrame(Assert.IsType<MessageLoadingListBoxItem>(list.ContainerFromItem(placeholder)).Skeleton);
                Assert.Equal(new Thickness(3.5), reattachedFrame.BorderThickness);
                Assert.Equal(reattachedFrame.BorderThickness, reattachedSkeleton.BorderThickness);
                SetThickness(configuration.Settings, kind, 5.5);
                runtime.NotifyAppearanceChanged();
                Pump(window);
                Assert.Equal(new Thickness(5.5), reattachedFrame.BorderThickness);
                Assert.Equal(reattachedFrame.BorderThickness, reattachedSkeleton.BorderThickness);
            }
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>未指定新设置时三类消息及骨架都保持原有默认粗细。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.SuperChat)]
    [InlineData(MessageKind.Gift)]
    [InlineData(MessageKind.Guard)]
    [InlineData(MessageKind.Danmu)]
    public void StandaloneRowsAndSkeletonsKeepDefaultThickness(MessageKind kind)
    {
        var row = new MessageFeedRow(kind);
        var skeleton = new MessageFeedSkeleton { Kind = kind };
        var window = new Window { Content = new StackPanel { Children = { row, skeleton } }, Width = 760, Height = 420 };
        try
        {
            window.Show();
            Pump(window);
            var expected = new Thickness(kind == MessageKind.Danmu ? 0 : 2);
            Assert.Equal(expected, FindFrame(row).BorderThickness);
            Assert.Equal(expected, FindFrame(skeleton).BorderThickness);
        }
        finally
        {
            window.Close();
        }
    }

    private static Border FindFrame(Control row)
    {
        return row.GetVisualDescendants().OfType<Border>().Single(border => border.Child is Grid grid &&
            (grid.Classes.Contains("scFeedLayout") || grid.Classes.Contains("danmuFeedLayout")));
    }

    private static void SetThickness(PanelSettings settings, MessageKind kind, double value)
    {
        switch (kind)
        {
            case MessageKind.SuperChat:
                settings.MessageBorders.SuperChat = value;
                break;
            case MessageKind.Gift:
                settings.MessageBorders.Gift = value;
                break;
            case MessageKind.Guard:
                settings.MessageBorders.Guard = value;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
