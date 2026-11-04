using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Tests.Windowing;

internal sealed class HeaderActionsTestView : UserControl, IWorkbenchHeaderActions
{
    internal HeaderActionsTestView()
    {
        HeaderActionsTemplate = new FuncDataTemplate<HeaderActionsTestView>((_, _) =>
            new Button { Name = "TestHeaderAction", Content = "Action" });
    }

    /// <inheritdoc />
    public IDataTemplate HeaderActionsTemplate { get; }
}
