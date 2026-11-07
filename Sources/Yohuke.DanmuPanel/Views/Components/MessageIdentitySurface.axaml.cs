using Avalonia.Controls;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed partial class MessageIdentitySurface : DockPanel
{
    protected override Type StyleKeyOverride => typeof(DockPanel);

    public MessageIdentitySurface()
    {
        InitializeComponent();
    }
}
