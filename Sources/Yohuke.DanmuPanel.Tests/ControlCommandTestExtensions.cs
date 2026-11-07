using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Yohuke.DanmuPanel.Tests;

internal static class ControlCommandTestExtensions
{
    internal static void ExecuteBoundCommand(this Button button)
    {
        Execute(button.Command, button.CommandParameter);
        if (button.GetVisualRoot() is Window window)
        {
            window.UpdateLayout();
        }
    }

    internal static void ExecuteBoundCommand(this MenuItem item)
    {
        Execute(item.Command, item.CommandParameter);
    }

    private static void Execute(ICommand? command, object? parameter)
    {
        Assert.NotNull(command);
        Assert.True(command.CanExecute(parameter));
        command.Execute(parameter);
        Dispatcher.UIThread.RunJobs();
    }
}
