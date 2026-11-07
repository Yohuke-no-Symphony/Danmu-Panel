using Avalonia.Controls;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.ViewModels.Workspace;
using Yohuke.DanmuPanel.Views;

namespace Yohuke.DanmuPanel.Windowing;

internal sealed class ShellLayoutNameWindow : Window
{
    internal ShellLayoutNameWindow()
    {
        Width = 400;
        Height = 180;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Bind(TitleProperty, Localization.Observe("Shell.Layout.SaveAs"));
        var surface = new ShellLayoutNameSurface { DataContext = new ShellLayoutNameViewModel(value => Close(value)) };
        Content = surface;
        WindowChrome.AttachFrame(this);
        Opened += (_, _) => surface.FindControl<TextBox>("LayoutNameInput")!.Focus();
    }
}
