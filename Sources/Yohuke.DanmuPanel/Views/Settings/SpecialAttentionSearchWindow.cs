using Avalonia.Controls;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.ViewModels.Settings;
using Yohuke.DanmuPanel.Windowing;

namespace Yohuke.DanmuPanel.Views.Settings;

internal sealed class SpecialAttentionSearchWindow : Window
{
    internal SpecialAttentionEditorViewModel Model { get; }

    internal SpecialAttentionSearchWindow(SpecialAttentionEditorViewModel model)
    {
        Model = model;
        Width = 680;
        Height = 600;
        MinWidth = 480;
        MinHeight = 440;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Bind(TitleProperty, Localization.Observe("SpecialAttention.FindUsers"));
        var surface = new SpecialAttentionSearchSurface { DataContext = model };
        surface.FindControl<Button>("CloseSpecialAttentionSearchButton")!.Click += (_, _) => Close();
        Content = surface;
        Closed += (_, _) => Model.CancelSearch();
        WindowChrome.AttachFrame(this);
    }
}
