using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using Lucide.Avalonia;
using Yohuke.DanmuPanel.I18n;

namespace Yohuke.DanmuPanel.Views;

internal static class Ui
{
    internal static TextBlock Text(string key)
    {
        var text = new TextBlock();
        text.Bind(TextBlock.TextProperty, Localization.Observe(key));
        return text;
    }

    internal static Button Button(string key, Func<Task> clicked, Action<Exception> failed)
    {
        var button = new Button();
        button.Bind(ContentControl.ContentProperty, Localization.Observe(key));
        ConnectCommand(button, clicked, failed);
        return button;
    }

    private static void ConnectCommand(Button button, Func<Task> clicked, Action<Exception> failed)
    {
        button.Command = new AsyncRelayCommand(async () =>
        {
            try
            {
                await clicked();
            }
            catch (Exception exception)
            {
                failed(exception);
            }
        });
    }

    internal static StackPanel Field(string key, Control control)
    {
        return new() { Spacing = 5, Margin = new Thickness(0, 0, 0, 10), Children = { Text(key), control } };
    }

    internal static Button IconButton(string key, LucideIconKind icon, Func<Task> clicked, Action<Exception> failed)
    {
        var button = new Button { Content = new LucideIcon { Kind = icon, Size = 16 } };
        ConnectCommand(button, clicked, failed);
        button.Classes.Add("iconButton");
        button.Bind(ToolTip.TipProperty, Localization.Observe(key));
        button.Bind(AutomationProperties.NameProperty, Localization.Observe(key));
        return button;
    }
}
