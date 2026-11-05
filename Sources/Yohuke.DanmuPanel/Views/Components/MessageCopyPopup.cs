using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives.PopupPositioning;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed class MessageCopyPopup : Flyout
{
    private readonly MessageCopySurface surface;

    internal MessageCopyPopup(string original, string translation, Action<Exception> reportError)
    {
        surface = new();
        surface.SetMessage(original, translation);
        Content = surface;
        Placement = PlacementMode.AnchorAndGravity;
        PlacementAnchor = PopupAnchor.BottomLeft;
        PlacementGravity = PopupGravity.BottomRight;
        OverlayDismissEventPassThrough = false;
        FlyoutPresenterClasses.Add("messageCopyPopup");
        surface.CopyRequested += async text =>
        {
            try
            {
                await TopLevel.GetTopLevel(surface)!.Clipboard!.SetTextAsync(text);
                Hide();
            }
            catch (Exception error)
            {
                reportError(error);
            }
        };
        surface.CloseRequested += (_, _) => Hide();
        Opened += (_, _) => surface.FocusOriginal();
    }

    internal void Show(Control owner, Rect anchorBounds)
    {
        surface.MaxWidth = Math.Max(0, TopLevel.GetTopLevel(owner)!.ClientSize.Width - 32);
        Popup.PlacementRect = anchorBounds;
        ShowAt(owner);
    }
}
