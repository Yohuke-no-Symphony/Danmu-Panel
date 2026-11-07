using Avalonia.Controls;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed partial class SkeletonIdentitySurface : DockPanel
{
    protected override Type StyleKeyOverride => typeof(DockPanel);

    public SkeletonIdentitySurface()
    {
        InitializeComponent();
    }
}
