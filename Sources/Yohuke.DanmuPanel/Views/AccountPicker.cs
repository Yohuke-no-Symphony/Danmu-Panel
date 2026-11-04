using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.State;

namespace Yohuke.DanmuPanel.Views;

internal static class AccountPicker
{
    internal static ComboBox Create(PanelConfiguration configuration, string? selectedId)
    {
        var choices = new[] { new AccountChoice(null, Localization.Get("Panel.InheritAccount")) }
            .Concat(configuration.Accounts.Select(account => new AccountChoice(account.Id, account.Name))).ToArray();
        return new()
        {
            ItemsSource = choices, SelectedItem = choices.FirstOrDefault(choice => choice.Id == selectedId) ?? choices[0],
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            ItemTemplate = new FuncDataTemplate<AccountChoice>((choice, _) => new TextBlock { Text = choice?.Name })
        };
    }

    internal static string? SelectedId(ComboBox picker)
    {
        return (picker.SelectedItem as AccountChoice)?.Id;
    }
}
