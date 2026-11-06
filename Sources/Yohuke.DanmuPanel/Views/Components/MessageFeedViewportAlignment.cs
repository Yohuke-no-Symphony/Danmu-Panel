using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed class MessageFeedViewportAlignment : IDisposable
{
    private const double ALIGNMENT_EPSILON = 0.01;
    private readonly ListBox pinnedList;
    private readonly ListBox scrollingList;
    private readonly Thickness pinnedMargin;
    private readonly Thickness scrollingMargin;
    private ScrollViewer? pinnedViewer;
    private ScrollViewer? scrollingViewer;
    private ScrollContentPresenter? pinnedPresenter;
    private ScrollContentPresenter? scrollingPresenter;
    private bool isUpdating;
    private bool isDisposed;

    internal MessageFeedViewportAlignment(ListBox pinnedList, ListBox scrollingList)
    {
        this.pinnedList = pinnedList;
        this.scrollingList = scrollingList;
        pinnedMargin = pinnedList.Margin;
        scrollingMargin = scrollingList.Margin;
        pinnedList.TemplateApplied += OnTemplateApplied;
        scrollingList.TemplateApplied += OnTemplateApplied;
        scrollingList.LayoutUpdated += OnLayoutUpdated;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }
        isDisposed = true;
        pinnedList.TemplateApplied -= OnTemplateApplied;
        scrollingList.TemplateApplied -= OnTemplateApplied;
        scrollingList.LayoutUpdated -= OnLayoutUpdated;
        pinnedList.Margin = pinnedMargin;
        scrollingList.Margin = scrollingMargin;
        pinnedViewer = null;
        scrollingViewer = null;
        pinnedPresenter = null;
        scrollingPresenter = null;
    }

    private void OnTemplateApplied(object? sender, TemplateAppliedEventArgs args)
    {
        pinnedViewer = null;
        scrollingViewer = null;
        pinnedPresenter = null;
        scrollingPresenter = null;
    }

    private void OnLayoutUpdated(object? sender, EventArgs args)
    {
        if (isDisposed || isUpdating)
        {
            return;
        }
        var pinnedGutter = GetGutter(pinnedList, pinnedMargin, ref pinnedViewer, ref pinnedPresenter);
        var scrollingGutter = GetGutter(scrollingList, scrollingMargin, ref scrollingViewer, ref scrollingPresenter);
        var sharedGutter = Math.Max(pinnedGutter ?? double.NegativeInfinity,
            scrollingGutter ?? double.NegativeInfinity);
        isUpdating = true;
        try
        {
            ApplyMargin(pinnedList, pinnedMargin, pinnedGutter.HasValue ? sharedGutter - pinnedGutter.Value : 0);
            ApplyMargin(scrollingList, scrollingMargin,
                scrollingGutter.HasValue ? sharedGutter - scrollingGutter.Value : 0);
        }
        finally
        {
            isUpdating = false;
        }
    }

    private static double? GetGutter(ListBox list, Thickness margin, ref ScrollViewer? viewer,
        ref ScrollContentPresenter? presenter)
    {
        var root = list.GetVisualRoot();
        if (!list.IsEffectivelyVisible || root is null)
        {
            return null;
        }
        if (viewer is null || !ReferenceEquals(viewer.GetVisualRoot(), root))
        {
            viewer = list.GetVisualDescendants().OfType<ScrollViewer>()
                .FirstOrDefault(candidate => ReferenceEquals(candidate.TemplatedParent, list));
            presenter = null;
        }
        if (viewer is null)
        {
            return null;
        }
        if (presenter is null || !ReferenceEquals(presenter.GetVisualRoot(), root))
        {
            var templateOwner = viewer;
            presenter = viewer.GetVisualDescendants().OfType<ScrollContentPresenter>()
                .FirstOrDefault(candidate => ReferenceEquals(candidate.TemplatedParent, templateOwner));
        }
        if (presenter is null || presenter.Bounds.Width <= 0)
        {
            return null;
        }
        return margin.Right + Math.Max(0, viewer.Bounds.Width - presenter.Bounds.Width);
    }

    private static void ApplyMargin(ListBox list, Thickness original, double compensation)
    {
        var margin = new Thickness(original.Left, original.Top, original.Right + Math.Max(0, compensation), original.Bottom);
        var current = list.Margin;
        if (current.Left == margin.Left && current.Top == margin.Top && current.Bottom == margin.Bottom &&
            Math.Abs(current.Right - margin.Right) <= ALIGNMENT_EPSILON)
        {
            return;
        }
        list.Margin = margin;
    }
}
