using Avalonia;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Messages;
using Yohuke.DanmuPanel.ViewModels.Settings;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Views.Settings;

internal sealed class SpecialAttentionEditor : UserControl
{
    private readonly SpecialAttentionAvatarService avatars;
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
        Model.ConfigureAvatars(avatars, avatars.Register);
        var surface = new SpecialAttentionEditorSurface
        {
            DataContext = Model,
            UserNameColorInput = userNameColor,
            ContentColorInput = contentColor,
            OpenSearchCommand = new AsyncRelayCommand(() =>
            {
                PendingSearchDialog = ShowSearchDialogAsync();
                return PendingSearchDialog;
            })
        };
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
        var dialog = new SpecialAttentionSearchWindow(Model);
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

}
