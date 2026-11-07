using Avalonia;
using Avalonia.Controls;

namespace Yohuke.DanmuPanel.Views.Settings;

internal sealed class AccountSettingsRow : UserControl
{
    public AccountSettingsRow()
    {
        var surface = new AccountSettingsRowSurface();
        ContextMenu = surface.ContextMenu;
        surface.ContextMenu = null;
        ContextMenu!.Bind(DataContextProperty, this.GetObservable(DataContextProperty));
        Content = surface;
    }
}
