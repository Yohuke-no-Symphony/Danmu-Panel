using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Yohuke.DanmuPanel.I18n;

namespace Yohuke.DanmuPanel.Windowing;

internal sealed class ShellLayoutNameWindow : Window
{
    private readonly TextBox nameInput;
    private readonly Button save;
    private readonly Button cancel;

    internal ShellLayoutNameWindow()
    {
        Width = 400;
        Height = 180;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        nameInput = new() { Margin = new(0, 0, 0, 16) };
        save = new() { IsDefault = true };
        cancel = new() { IsCancel = true };
        save.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(nameInput.Text))
            {
                Close(nameInput.Text.Trim());
            }
        };
        cancel.Click += (_, _) => Close(null);
        Content = new StackPanel
        {
            Margin = new(20),
            Children =
            {
                nameInput,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, save }
                }
            }
        };
        WindowChrome.AttachFrame(this);
        RefreshLanguage();
        Localization.LanguageChanged += OnLanguageChanged;
        Opened += (_, _) => nameInput.Focus();
        Closed += (_, _) => Localization.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RefreshLanguage();
    }

    private void RefreshLanguage()
    {
        Title = Localization.Get("Shell.Layout.SaveAs");
        nameInput.Watermark = Localization.Get("Shell.Layout.Name");
        save.Content = Localization.Get("Shell.Save");
        cancel.Content = Localization.Get("Shell.Cancel");
    }
}
