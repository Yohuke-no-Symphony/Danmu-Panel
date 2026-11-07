using Avalonia;
using Avalonia.Controls.Recycling;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Dock.Avalonia.Controls;
using Dock.Controls.ProportionalStackPanel;
using Dock.Model.Controls;
using Dock.Model.Core;
using Yohuke.DanmuPanel.Windowing;

namespace Yohuke.DanmuPanel.Layouts;

internal static class WorkbenchDockTemplateCatalog
{
    internal static void Install(DockControl host, WorkbenchControlRecycling recycling)
    {
        host.PropertyChanged += OnHostPropertyChanged;
        host.AutoCreateDataTemplates = false;
        ControlRecyclingDataTemplate.SetControlRecycling(host, recycling);
        host.DataTemplates.Add(new WorkbenchDockPanelTemplate());
        host.DataTemplates.Add(new FuncDataTemplate<IRootDock>(value => value is not null, (_, _) => new RootDockControl()));
        host.DataTemplates.Add(new FuncDataTemplate<IProportionalDock>(value => value is not null, (_, _) => new WorkbenchProportionalDockControl()));
        host.DataTemplates.Add(new FuncDataTemplate<IToolDock>(value => value is not null, (_, _) => new WorkbenchToolDockControl()));
        host.DataTemplates.Add(new FuncDataTemplate<IProportionalDockSplitter>(value => value is not null, (_, _) => new WorkbenchProportionalStackPanelSplitter
        {
            [!ProportionalStackPanelSplitter.IsResizingEnabledProperty] = new Binding(nameof(IProportionalDockSplitter.CanResize)),
            [!ProportionalStackPanelSplitter.PreviewResizeProperty] = new Binding(nameof(IProportionalDockSplitter.ResizePreview))
        }));
    }

    private static void OnHostPropertyChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (sender is DockControl host && e.Property == DockControl.IsDraggingDockProperty &&
            e.GetOldValue<bool>() && !e.GetNewValue<bool>())
        {
            WorkbenchTextInput.RequestNativeContextRecovery(host);
        }
    }

}
