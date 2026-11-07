using System.Net;
using Avalonia.Controls;
using Avalonia.Platform;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.BrowserLogin;
using Yohuke.DanmuPanel.Models.Logging;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Windowing;
using Yohuke.DanmuPanel.ViewModels;

namespace Yohuke.DanmuPanel.Views;

/// <summary>
/// 内嵌浏览器登录窗口。
/// </summary>
public partial class BrowserCookieLoginWindow : Window
{
    private readonly BrowserLoginPlatform platform;
    private readonly BrowserCookieLoginService loginService = new();
    private readonly Func<NativeWebView> createWebView;
    private NativeWebView? webView;
    private bool isClosed;
    public BrowserCookieLoginViewModel Model { get; }

    /// <summary>
    /// 初始化内嵌浏览器登录窗口。
    /// </summary>
    public BrowserCookieLoginWindow()
        : this(BrowserLoginPlatform.Bilibili)
    {
    }

    /// <summary>
    /// 初始化内嵌浏览器登录窗口。
    /// </summary>
    public BrowserCookieLoginWindow(BrowserLoginPlatform platform)
        : this(platform, () => new())
    {
    }

    internal BrowserCookieLoginWindow(BrowserLoginPlatform platform, Func<NativeWebView> createWebView)
    {
        this.platform = platform;
        this.createWebView = createWebView;
        Model = new(() => loginService.GetPlatformName(platform), () => webView?.GoBack(),
            () => webView?.GoForward(), () => webView?.Refresh(), () => Close(null),
            () => CaptureCookiesAsync(ReadBrowserCookiesAsync));
        InitializeComponent();
        DataContext = Model;
        WindowChrome.AttachFrame(this);
        Opened += BrowserCookieLoginWindow_OnOpened;
        Closed += BrowserCookieLoginWindow_OnClosed;
        Localization.LanguageChanged += OnLanguageChanged;
        ConfigureShell();
    }

    private void ConfigureShell()
    {
        Model.RefreshLocalization();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        ConfigureShell();
    }

    private void SetLocalizedStatus(string key, params object[] arguments)
    {
        Model.SetLocalizedStatus(key, arguments);
    }

    private async void BrowserCookieLoginWindow_OnOpened(object? sender, EventArgs e)
    {
        await Task.Yield();

        if (!isClosed)
        {
            CreateBrowser();
        }
    }

    private void BrowserCookieLoginWindow_OnClosed(object? sender, EventArgs e)
    {
        isClosed = true;
        Opened -= BrowserCookieLoginWindow_OnOpened;
        Closed -= BrowserCookieLoginWindow_OnClosed;
        Localization.LanguageChanged -= OnLanguageChanged;
        ReleaseBrowser();
    }

    private void ReleaseBrowser()
    {
        var currentWebView = webView;
        webView = null;

        if (currentWebView is not null)
        {
            currentWebView.EnvironmentRequested -= WebView_OnEnvironmentRequested;
            currentWebView.NavigationCompleted -= WebView_OnNavigationCompleted;

            try
            {
                currentWebView.Stop();
            }
            catch (Exception exception)
            {
                StartupLog.Append(Localization.Format("UI.BrowserCookieLoginWindow.StopFailed", platform), AppLogLevel.ERROR, Localization.Get("UI.BrowserCookieLoginWindow.LogSource"), exception);
            }
        }

        WebViewHost.Child = null;
    }

    private void CreateBrowser()
    {
        try
        {
            var loginUri = loginService.GetLoginUri(platform);
            webView = createWebView();
            webView.EnvironmentRequested += WebView_OnEnvironmentRequested;
            webView.NavigationCompleted += WebView_OnNavigationCompleted;
            webView.Source = loginUri;
            WebViewHost.Child = webView;
            Model.IsPlaceholderVisible = false;
            StartupLog.Append(Localization.Format("UI.BrowserCookieLoginWindow.BrowserCreated", platform));
        }
        catch (Exception exception)
        {
            ReleaseBrowser();
            Model.BrowserInitializationFailed = true;
            SetLocalizedStatus("UI.BrowserCookieLoginWindow.InitializationFailedWithDetail", new Func<string>(() => LocalizedExceptionText.Get(exception)));
            WebViewHost.Child = WebViewPlaceholderText;
            Model.IsPlaceholderVisible = true;
            StartupLog.Append(Localization.Format("UI.BrowserCookieLoginWindow.InitializeLogFailed", platform), AppLogLevel.ERROR, Localization.Get("UI.BrowserCookieLoginWindow.LogSource"), exception);
        }
    }

    private void WebView_OnEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs e)
    {
        ConfigureBrowserEnvironment(e);
    }

    internal void ConfigureBrowserEnvironment(WebViewEnvironmentRequestedEventArgs e)
    {
        e.EnableDevTools = false;

        var profileDirectory = loginService.GetProfileDirectory(platform);
        var cacheDirectory = Path.Combine(profileDirectory, "Cache");
        Directory.CreateDirectory(profileDirectory);
        Directory.CreateDirectory(cacheDirectory);

        switch (e)
        {
            case WindowsWebView2EnvironmentRequestedEventArgs windows:
                windows.UserDataFolder = profileDirectory;
                windows.ProfileName = $"YohukeDanmuPanel{platform}";
                windows.IsInPrivateModeEnabled = platform == BrowserLoginPlatform.Bilibili;
                break;
            case AppleWKWebViewEnvironmentRequestedEventArgs apple:
                apple.NonPersistentDataStore = true;
                break;
            case GtkWebViewEnvironmentRequestedEventArgs gtk:
                gtk.BaseDataDirectory = profileDirectory;
                gtk.BaseCacheDirectory = cacheDirectory;
                break;
            case LinuxWpeWebViewEnvironmentRequestedEventArgs wpe:
                wpe.DataDirectory = profileDirectory;
                wpe.CacheDirectory = cacheDirectory;
                break;
        }
    }

    private void WebView_OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        if (!isClosed)
        {
            Model.RawStatus = webView?.Source.ToString() ?? loginService.GetLoginUri(platform).ToString();
        }
    }

    internal async Task CaptureCookiesAsync(Func<Task<IEnumerable<Cookie>>> readCookies)
    {
        if (isClosed || Model.IsCapturing)
        {
            return;
        }

        Model.IsCapturing = true;

        try
        {
            var cookies = await readCookies();

            if (isClosed)
            {
                return;
            }

            var result = loginService.CreateResult(platform, cookies);
            Close(result);
        }
        catch (Exception exception)
        {
            if (!isClosed)
            {
                SetLocalizedStatus("UI.BrowserCookieLoginWindow.CaptureFailed", new Func<string>(() => LocalizedExceptionText.Get(exception)));
                StartupLog.Append(Localization.Format("UI.BrowserCookieLoginWindow.ReadSignInLogFailed", platform), AppLogLevel.ERROR, Localization.Get("UI.BrowserCookieLoginWindow.LogSource"), exception);
            }
        }
        finally
        {
            Model.IsCapturing = false;

        }
    }

    private async Task<IEnumerable<Cookie>> ReadBrowserCookiesAsync()
    {
        var currentWebView = webView;

        if (currentWebView is null)
        {
            throw LocalizedExceptionText.Create("UI.BrowserCookieLoginWindow.BrowserNotReady",
                message => new InvalidOperationException(message));
        }

        var cookieManager = currentWebView.TryGetCookieManager()
                            ?? throw LocalizedExceptionText.Create("UI.BrowserCookieLoginWindow.CookieReadingUnsupported",
                                message => new InvalidOperationException(message));
        return await cookieManager.GetCookiesAsync();
    }
}
