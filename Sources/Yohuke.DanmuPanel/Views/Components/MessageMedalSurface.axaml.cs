using Avalonia.Controls;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed partial class MessageMedalSurface : Grid
{
    protected override Type StyleKeyOverride => typeof(Grid);

    public MessageMedalSurface()
    {
        InitializeComponent();
    }
}
