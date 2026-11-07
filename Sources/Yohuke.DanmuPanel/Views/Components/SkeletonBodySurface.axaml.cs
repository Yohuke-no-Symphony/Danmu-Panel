using Avalonia.Controls;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed partial class SkeletonBodySurface : StackPanel
{
    protected override Type StyleKeyOverride => typeof(StackPanel);

    public SkeletonBodySurface()
    {
        InitializeComponent();
    }
}
