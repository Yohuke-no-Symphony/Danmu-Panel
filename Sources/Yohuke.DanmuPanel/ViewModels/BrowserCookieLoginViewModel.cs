using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yohuke.DanmuPanel.I18n;

namespace Yohuke.DanmuPanel.ViewModels;

/// <summary>绑定浏览器登录窗口的本地化提示及操作状态。</summary>
public sealed partial class BrowserCookieLoginViewModel : ObservableObject
{
    private readonly Func<string> platformName;
    private string statusKey = "UI.BrowserCookieLoginWindow.SignInHint";
    private object[] statusArguments = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string? rawStatus;
    [ObservableProperty] private bool isCapturing;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlaceholderText))]
    private bool browserInitializationFailed;
    [ObservableProperty] private bool isPlaceholderVisible = true;
    public string TitleText => Localization.Format("UI.BrowserCookieLoginWindow.PlatformTitle", platformName());
    public string StatusText => RawStatus ?? Localization.Format(statusKey, statusArguments.Select(argument => argument is Func<string> provider ? provider() : argument).ToArray());
    public string PlaceholderText => Localization.Get(BrowserInitializationFailed
        ? "UI.BrowserCookieLoginWindow.InitializationFailed" : "UI.BrowserCookieLoginWindow.PreparingBrowser");
    public IRelayCommand BackCommand { get; }
    public IRelayCommand ForwardCommand { get; }
    public IRelayCommand RefreshCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IAsyncRelayCommand CaptureCommand { get; }

    /// <summary>连接原生浏览器和关闭窗口的视图操作。</summary>
    public BrowserCookieLoginViewModel(Func<string> platformName, Action back, Action forward, Action refresh,
        Action cancel, Func<Task> capture)
    {
        this.platformName = platformName;
        BackCommand = new RelayCommand(back);
        ForwardCommand = new RelayCommand(forward);
        RefreshCommand = new RelayCommand(refresh);
        CancelCommand = new RelayCommand(cancel);
        CaptureCommand = new AsyncRelayCommand(capture);
    }

    internal void SetLocalizedStatus(string key, params object[] arguments)
    {
        statusKey = key;
        statusArguments = arguments;
        RawStatus = null;
        OnPropertyChanged(nameof(StatusText));
    }

    internal void RefreshLocalization()
    {
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(PlaceholderText));
    }
}
