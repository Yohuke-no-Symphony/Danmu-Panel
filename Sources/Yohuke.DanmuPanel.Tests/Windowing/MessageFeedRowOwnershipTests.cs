using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Tests.Windowing;

/// <summary>验证虚拟化行的稳定逻辑归属与正常视觉回收，覆盖源和模板替换。</summary>
public sealed class MessageFeedRowOwnershipTests
{
    /// <summary>滚动时集合通知与逻辑树保持一致，回收行脱离视觉树并清空当前消息。</summary>
    [AvaloniaFact]
    public void ScrollingKeepsLogicalCollectionEventsConsistentAndRowsHaveOneOwner()
    {
        var source = Source();
        var list = List(source);
        var window = new Window { Width = 600, Height = 300, Content = list };
        try
        {
            window.Show();
            Pump(window);
            var children = ((ILogical)list).LogicalChildren;
            var mirror = children.ToList();
            ((INotifyCollectionChanged)children).CollectionChanged += (_, args) =>
            {
                if (args.Action == NotifyCollectionChangedAction.Remove)
                {
                    foreach (ILogical item in args.OldItems!)
                    {
                        Assert.True(mirror.Remove(item));
                    }
                }
                else if (args.Action == NotifyCollectionChangedAction.Add)
                {
                    mirror.InsertRange(args.NewStartingIndex, args.NewItems!.Cast<ILogical>());
                }
                Assert.Equal(children, mirror);
            };
            Scroll(window, list);
            AssertOwners(list);
            Assert.InRange(children.OfType<MessageFeedRow>().Count(), 1, 64);
            Assert.Contains(children.OfType<MessageFeedRow>(), row => row.GetVisualRoot() is null);
            foreach (var row in children.OfType<MessageFeedRow>().Where(row => row.GetVisualRoot() is null))
            {
                Assert.Null(row.DataContext);
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>清空和裁剪至空释放行槽，旧容器重新使用时恢复唯一归属。</summary>
    [AvaloniaFact]
    public void SourceResetAndEmptyCropDropSlotsAndReusedContainersRegisterAgain()
    {
        var source = Source();
        var list = List(source);
        var window = new Window { Width = 600, Height = 300, Content = list };
        try
        {
            window.Show();
            Pump(window);
            Scroll(window, list);
            var oldRows = ((ILogical)list).LogicalChildren.OfType<MessageFeedRow>().ToArray();
            source.Clear();
            Pump(window);
            Assert.Empty(((ILogical)list).LogicalChildren.OfType<MessageFeedRow>());
            Assert.All(oldRows, row =>
            {
                Assert.Null(row.Parent);
                Assert.Null(row.DataContext);
                Assert.Null(row.GetVisualRoot());
            });
            foreach (var item in Source(20))
            {
                source.Add(item);
            }
            Pump(window);
            AssertOwners(list);
            Assert.NotEmpty(list.GetRealizedContainers());
            while (source.Count > 0)
            {
                source.RemoveAt(source.Count - 1);
            }
            Pump(window);
            Assert.Empty(((ILogical)list).LogicalChildren.OfType<MessageFeedRow>());
            foreach (var item in Source(20))
            {
                source.Add(item);
            }
            Pump(window);
            AssertOwners(list);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>替换数据源、面板或模板时释放旧行的归属与绑定。</summary>
    [AvaloniaFact]
    public void SourcePanelAndTemplateReplacementReleaseOldSlots()
    {
        var list = List(Source());
        var window = new Window { Width = 600, Height = 300, Content = list };
        try
        {
            window.Show();
            Pump(window);
            Scroll(window, list);
            var oldRows = ((ILogical)list).LogicalChildren.OfType<MessageFeedRow>().ToArray();
            list.ItemsSource = Source(30);
            Pump(window);
            AssertOwners(list);
            Assert.All(oldRows.Where(row => !((ILogical)list).LogicalChildren.Contains(row)), row => Assert.Null(row.Parent));
            oldRows = ((ILogical)list).LogicalChildren.OfType<MessageFeedRow>().ToArray();
            list.ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel());
            Pump(window);
            AssertOwners(list);
            Assert.All(oldRows, row =>
            {
                Assert.Null(row.Parent);
                Assert.Null(row.DataContext);
                Assert.Null(row.GetVisualRoot());
            });
            oldRows = ((ILogical)list).LogicalChildren.OfType<MessageFeedRow>().ToArray();
            var template = list.Template;
            list.Template = null;
            Pump(window);
            list.Template = template;
            Pump(window);
            AssertOwners(list);
            Assert.All(oldRows, row => Assert.Null(row.Parent));
            Assert.NotEmpty(list.GetRealizedContainers());
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>脱离与重新挂载列表保持逻辑归属，视觉生命周期和后续滚动均正常。</summary>
    [AvaloniaFact]
    public void DetachingAndReattachingListPreservesSlotOwnershipAndCleansVisualRoots()
    {
        var list = List(Source());
        var window = new Window { Width = 600, Height = 300, Content = list };
        try
        {
            window.Show();
            Pump(window);
            Scroll(window, list);
            var rows = ((ILogical)list).LogicalChildren.OfType<MessageFeedRow>().ToArray();
            window.Content = null;
            Pump(window);
            Assert.All(rows, row =>
            {
                Assert.Same(list, row.Parent);
                Assert.Null(row.GetVisualRoot());
                Assert.False(((ILogical)row).IsAttachedToLogicalTree);
            });
            window.Content = list;
            Pump(window);
            AssertOwners(list);
            Scroll(window, list);
            AssertOwners(list);
            Assert.InRange(((ILogical)list).LogicalChildren.OfType<MessageFeedRow>().Count(), 1, 64);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>未挂载期间清空数据源也会释放旧行，反复重挂不会积累已废弃槽。</summary>
    [AvaloniaFact]
    public void DetachedSourceResetReleasesSlotsAndRepeatedReattachStaysBounded()
    {
        var source = Source();
        var list = List(source);
        var window = new Window { Width = 600, Height = 300, Content = list };
        try
        {
            window.Show();
            Pump(window);
            Scroll(window, list);
            for (var cycle = 0; cycle < 5; cycle++)
            {
                var oldRows = ((ILogical)list).LogicalChildren.OfType<MessageFeedRow>().ToArray();
                window.Content = null;
                Pump(window);
                source.Clear();
                Assert.Empty(((ILogical)list).LogicalChildren.OfType<MessageFeedRow>());
                Assert.All(oldRows, row =>
                {
                    Assert.Null(row.Parent);
                    Assert.Null(row.DataContext);
                });
                foreach (var item in Source(100))
                {
                    source.Add(item);
                }
                window.Content = list;
                Pump(window);
                AssertOwners(list);
                Scroll(window, list);
                Assert.InRange(((ILogical)list).LogicalChildren.OfType<MessageFeedRow>().Count(), 1, 64);
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>持续观察集合变化不会让长寿命消息源保留已失去引用的列表。</summary>
    [AvaloniaFact]
    public void LongLivedSourceDoesNotRetainUnrootedList()
    {
        var source = Source();
        var listReference = CreateUnrootedList(source);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.False(listReference.TryGetTarget(out _));
        GC.KeepAlive(source);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<MessageFeedListBox> CreateUnrootedList(ObservableCollection<MessageItemViewModel> source)
    {
        var list = List(source);
        return new(list);
    }

    private static void AssertOwners(MessageFeedListBox list)
    {
        var rows = ((ILogical)list).LogicalChildren.OfType<MessageFeedRow>().ToArray();
        Assert.Equal(rows.Length, rows.Distinct().Count());
        foreach (var row in rows)
        {
            Assert.Same(list, row.Parent);
            var host = Assert.IsType<MessageFeedRowHost>(row.GetVisualParent());
            Assert.Empty(((ILogical)host).LogicalChildren);
            Assert.Same(row.GetVisualRoot(), host.GetVisualRoot());
        }
        foreach (var container in list.GetRealizedContainers().OfType<MessageFeedListBoxItem>())
        {
            Assert.Contains(container.Row, rows);
            Assert.Same(container.DataContext, container.Row.DataContext);
            Assert.IsType<MessageItemViewModel>(container.Row.DataContext);
        }
    }

    private static MessageFeedListBox List(ObservableCollection<MessageItemViewModel> source)
    {
        return new(() => new(MessageKind.Danmu)) { ItemsSource = source };
    }

    private static ObservableCollection<MessageItemViewModel> Source(int count = 400)
    {
        var pinyin = new PinyinService();
        return new(Enumerable.Range(0, count).Select(index => new MessageItemViewModel(new()
        {
            Id = "message-" + index,
            Kind = MessageKind.Danmu,
            UserName = "viewer " + index,
            Content = "message " + index
        }, new PanelSettings(), pinyin)));
    }

    private static void Scroll(Window window, MessageFeedListBox list)
    {
        var viewer = Assert.Single(list.GetVisualDescendants().OfType<ScrollViewer>(), item => item.TemplatedParent == list);
        var presenter = Assert.Single(viewer.GetVisualDescendants().OfType<ScrollContentPresenter>(),
            item => ReferenceEquals(item.TemplatedParent, viewer));
        for (var index = 0; index < 30; index++)
        {
            presenter.RaiseEvent(new ScrollGestureEventArgs(ScrollGestureEventArgs.GetNextFreeId(), new(0, 80)));
            Pump(window);
        }
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
