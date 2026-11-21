using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Windowing;

namespace Yohuke.DanmuPanel.Views;

/// <summary>让用户完成 B 站短信登录要求的人机验证。</summary>
public partial class BilibiliCaptchaWindow : Window
{
    private static readonly Uri PAGE_BASE_URI = new("https://bilibili-login.danmupanel.invalid/");
    private readonly BilibiliLoginChallenge? challenge;
    private readonly string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer loadTimer = new() { Interval = TimeSpan.FromSeconds(25) };
    private NativeWebView? browser;
    private Bitmap? image;
    private bool closed;

    /// <summary>初始化人机验证窗口。</summary>
    public BilibiliCaptchaWindow()
    {
        InitializeComponent();
        WindowChrome.AttachFrame(this);
    }

    internal BilibiliCaptchaWindow(BilibiliLoginChallenge challenge) : this()
    {
        this.challenge = challenge;
        if (challenge.Type == "img")
        {
            MinHeight = 0;
            MinWidth = 0;
            Width = 360;
            Height = 284;
            CanResize = false;
        }
    }

    /// <inheritdoc />
    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (challenge is null)
        {
            ShowLoadError();
            return;
        }

        try
        {
            if (challenge.Type == "img")
            {
                var token = lifetime.Token;
                var bytes = await BilibiliCaptchaService.LoadImageAsync(challenge, token);
                token.ThrowIfCancellationRequested();
                using var stream = new MemoryStream(bytes);
                image = new Bitmap(stream);
                CaptchaImage.Source = image;
                ImageChallengePanel.IsVisible = true;
                CaptchaStatus.IsVisible = false;
                ImageCode.Focus();
            }
            else
            {
                OpenGeetest();
            }
        }
        catch (OperationCanceledException) when (closed)
        {
        }
        catch (Exception)
        {
            if (!closed)
            {
                ShowLoadError();
            }
        }
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        closed = true;
        lifetime.Cancel();
        lifetime.Dispose();
        loadTimer.Stop();
        loadTimer.Tick -= OnLoadTimeout;
        ReleaseBrowser();
        CaptchaImage.Source = null;
        image?.Dispose();
        ImageCode.Text = "";
        base.OnClosed(e);
    }

    private void OpenGeetest()
    {
        browser = new();
        browser.EnvironmentRequested += Browser_OnEnvironmentRequested;
        browser.AdapterCreated += Browser_OnAdapterCreated;
        browser.NavigationStarted += Browser_OnNavigationStarted;
        browser.NavigationCompleted += Browser_OnNavigationCompleted;
        browser.NewWindowRequested += Browser_OnNewWindowRequested;
        browser.WebMessageReceived += Browser_OnWebMessageReceived;
        loadTimer.Tick += OnLoadTimeout;
        loadTimer.Start();
        BrowserHost.Child = browser;
    }

    private static void Browser_OnEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs e)
    {
        e.EnableDevTools = false;
        switch (e)
        {
            case AppleWKWebViewEnvironmentRequestedEventArgs apple:
                apple.NonPersistentDataStore = true;
                break;
            case WindowsWebView2EnvironmentRequestedEventArgs windows:
                windows.IsInPrivateModeEnabled = true;
                break;
        }
    }

    private void Browser_OnAdapterCreated(object? sender, EventArgs e)
    {
        if (browser is null || challenge is null)
        {
            return;
        }

        try
        {
            browser.NavigateToString(BilibiliCaptchaService.CreateGeetestPage(challenge, nonce), PAGE_BASE_URI);
        }
        catch (Exception)
        {
            Dispatcher.UIThread.Post(ShowLoadError);
        }
    }

    private static void Browser_OnNavigationStarted(object? sender, WebViewNavigationStartingEventArgs e)
    {
        e.Cancel = !IsCaptchaPage(e.Request);
    }

    private async void Browser_OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        if (sender is not NativeWebView view || view != browser || !IsCaptchaPage(e.Request))
        {
            return;
        }

        if (!e.IsSuccess)
        {
            Dispatcher.UIThread.Post(ShowLoadError);
            return;
        }

        try
        {
            await view.InvokeScript("window.reportCaptchaStatus?.()");
        }
        catch (Exception)
        {
            if (view == browser)
            {
                Dispatcher.UIThread.Post(ShowLoadError);
            }
        }
    }

    private static void Browser_OnNewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs e)
    {
        e.Handled = true;
    }

    private static bool IsCaptchaPage(Uri? request) =>
        request == PAGE_BASE_URI || request?.OriginalString == "about:blank";

    private void Browser_OnWebMessageReceived(object? sender, WebMessageReceivedEventArgs e)
    {
        if (challenge is null ||
            !BilibiliCaptchaService.TryReadGeetestMessage(e.Body, nonce, challenge, out var status, out var proof))
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (closed || browser is null)
            {
                return;
            }

            switch (status)
            {
                case "ready":
                    loadTimer.Stop();
                    CaptchaStatus.IsVisible = false;
                    break;
                case "error":
                    ShowLoadError();
                    break;
                case "verified" when proof is not null:
                    Close(proof);
                    break;
            }
        });
    }

    private void OnLoadTimeout(object? sender, EventArgs e) => ShowLoadError();

    private void ShowLoadError()
    {
        if (closed)
        {
            return;
        }
        loadTimer.Stop();
        ReleaseBrowser();
        CaptchaStatus.IsVisible = true;
        CaptchaStatus.Text = Localization.Get("UI.BilibiliCaptchaWindow.Unavailable");
    }

    private void ReleaseBrowser()
    {
        var current = browser;
        browser = null;
        if (current is null)
        {
            return;
        }

        current.EnvironmentRequested -= Browser_OnEnvironmentRequested;
        current.AdapterCreated -= Browser_OnAdapterCreated;
        current.NavigationStarted -= Browser_OnNavigationStarted;
        current.NavigationCompleted -= Browser_OnNavigationCompleted;
        current.NewWindowRequested -= Browser_OnNewWindowRequested;
        current.WebMessageReceived -= Browser_OnWebMessageReceived;
        try
        {
            current.Stop();
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            BrowserHost.Child = null;
        }
    }

    private void SubmitImageCode_OnClick(object? sender, RoutedEventArgs e)
    {
        if (challenge is { Type: "img" } && ImageCode.Text?.Trim() is { Length: > 0 } answer)
        {
            Close(new BilibiliChallengeProof { Token = challenge.Token, Captcha = answer });
        }
    }

    private void Cancel_OnClick(object? sender, RoutedEventArgs e) => Close();
}
