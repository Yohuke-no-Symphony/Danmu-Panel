using Avalonia.Controls;
using Avalonia.Input;
using Yohuke.DanmuPanel.ViewModels;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed class MessageFeedListBoxItem : ListBoxItem
{
    internal MessageFeedRow Row { get; }
    internal MessageFeedRowHost Host { get; }

    internal MessageFeedListBoxItem(MessageFeedRow row)
    {
        Row = row;
        Host = new(row);
        Content = Host;
    }

    internal void ConnectRowDataContext()
    {
        DataContextChanged += OnContainerDataContextChanged;
        Row.DataContext = DataContext as MessageItemViewModel;
    }

    internal void DisconnectRowDataContext()
    {
        DataContextChanged -= OnContainerDataContextChanged;
        Row.DataContext = null;
    }

    private void OnContainerDataContextChanged(object? sender, EventArgs args)
    {
        Row.DataContext = DataContext as MessageItemViewModel;
    }

    /// <inheritdoc/>
    protected override Type StyleKeyOverride => typeof(ListBoxItem);

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.RightButtonPressed)
        {
            base.OnPointerPressed(e);
        }
    }
}
