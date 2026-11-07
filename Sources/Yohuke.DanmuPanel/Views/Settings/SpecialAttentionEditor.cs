using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Messages;
using Yohuke.DanmuPanel.ViewModels.Settings;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Views.Settings;

internal sealed class SpecialAttentionEditor : UserControl
{
    private readonly SpecialAttentionAvatarService avatars;
    private readonly IDataTemplate searchResultsTemplate;
    private SpecialAttentionSearchWindow? searchWindow;
    internal SpecialAttentionEditorViewModel Model { get; }
    internal Task PendingSearchDialog { get; private set; } = Task.CompletedTask;

    internal SpecialAttentionEditor(PanelRuntime runtime, ColorInput userNameColor, ColorInput contentColor,
        IBilibiliUserSearchService? search = null, IUserAvatarService? userAvatars = null)
    {
        Name = "SpecialAttentionEditor";
        Model = new(runtime.Configuration.Settings.SpecialAttention, search ?? new BilibiliUserSearchService(),
            () => runtime.Configuration.Accounts.FirstOrDefault(account => account.Id == runtime.Configuration.Settings.DefaultAccountId)?.Cookie);
        avatars = new(runtime.Avatars, userAvatars ?? runtime.UserAvatars);
        var surface = new SpecialAttentionEditorSurface { DataContext = Model };
        surface.FindControl<ContentControl>("SpecialAttentionUserNameColorInputHost")!.Content = userNameColor;
        surface.FindControl<ContentControl>("SpecialAttentionContentColorInputHost")!.Content = contentColor;
        searchResultsTemplate = new FuncDataTemplate<UserSearchResult>((result, _) =>
            {
                if (result is null)
                {
                    return new TextBlock();
                }
                avatars.Register(result);
                return CreateUserRow(result.UserUid, result.DisplayName, result, Model.AddSearchedUserCommand, true);
            });
        surface.FindControl<Button>("OpenSpecialAttentionSearchButton")!.Click += async (_, _) =>
        {
            PendingSearchDialog = ShowSearchDialogAsync();
            await PendingSearchDialog;
        };
        surface.FindControl<ItemsControl>("SpecialAttentionUsers")!.ItemTemplate =
            new FuncDataTemplate<SpecialAttentionUser>((user, _) => user is null ? new TextBlock() :
                CreateUserRow(user.UserUid, user.DisplayName, user, Model.RemoveUserCommand, false));
        Content = surface;
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Localization.LanguageChanged += OnLanguageChanged;
        Model.RefreshLocalization();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Localization.LanguageChanged -= OnLanguageChanged;
        if (searchWindow?.IsVisible == true)
        {
            searchWindow.Close();
        }
        Model.CancelSearch();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        Model.RefreshLocalization();
    }

    private async Task ShowSearchDialogAsync()
    {
        if (searchWindow is { } existing)
        {
            existing.Activate();
            return;
        }
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }
        var dialog = new SpecialAttentionSearchWindow(Model, searchResultsTemplate);
        searchWindow = dialog;
        try
        {
            await dialog.ShowDialog(owner);
        }
        finally
        {
            searchWindow = null;
        }
    }

    private Control CreateUserRow(long userUid, string displayName, object item, ICommand command, bool adding)
    {
        var avatar = new UserAvatarView(avatars) { UserUid = userUid, Width = 32, Height = 32 };
        var name = new TextBlock
        {
            Text = displayName.Length == 0 ? userUid.ToString(CultureInfo.InvariantCulture) : displayName,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        ToolTip.SetTip(name, name.Text);
        var identity = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                name,
                new TextBlock { Text = "UID " + userUid.ToString(CultureInfo.InvariantCulture), FontSize = 11, Classes = { "muted" } }
            }
        };
        var action = new Button
        {
            Name = adding ? "AddSpecialAttentionSearchResultButton" : "RemoveSpecialAttentionUserButton",
            Command = command,
            CommandParameter = item,
            VerticalAlignment = VerticalAlignment.Center
        };
        action.Bind(ContentControl.ContentProperty, Localization.Observe(adding ? "SpecialAttention.AddUser" : "SpecialAttention.RemoveUser"));
        var layout = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 10 };
        layout.Children.Add(avatar);
        Grid.SetColumn(identity, 1);
        layout.Children.Add(identity);
        Grid.SetColumn(action, 2);
        layout.Children.Add(action);
        var row = new Border { Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 6), Child = layout, DataContext = item };
        row.Classes.Add("panel");
        return row;
    }
}
