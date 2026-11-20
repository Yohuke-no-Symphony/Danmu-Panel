using CommunityToolkit.Mvvm.ComponentModel;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.State;

namespace Yohuke.DanmuPanel.ViewModels;

internal sealed class StartupViewModel : ObservableObject
{
    internal StartupStage Stage { get; private set; } = StartupStage.Configuration;

    public string StatusText => Localization.Get($"Panel.Startup.{Stage}");

    public bool CanCancel => Stage != StartupStage.Cancelling;

    internal void ShowStage(StartupStage stage)
    {
        if (Stage == stage)
        {
            return;
        }

        var couldCancel = CanCancel;
        Stage = stage;
        RefreshLanguage();
        if (CanCancel != couldCancel)
        {
            OnPropertyChanged(nameof(CanCancel));
        }
    }

    internal void RefreshLanguage()
    {
        OnPropertyChanged(nameof(StatusText));
    }
}
