using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.ViewModels.Settings;
using Yohuke.DanmuPanel.Windowing;

namespace Yohuke.DanmuPanel.Views;

/// <summary>应用内 B 站登录窗口。</summary>
public partial class BilibiliLoginWindow : Window
{
    private readonly BilibiliQrLoginViewModel qrLogin;
    private readonly BilibiliSmsLoginViewModel smsLogin;
    private readonly IDisposable titleBinding;
    private bool opened;

    /// <summary>初始化登录窗口。</summary>
    public BilibiliLoginWindow()
        : this(false)
    {
    }

    internal BilibiliLoginWindow(bool relogin, BilibiliQrLoginViewModel? qrLogin = null, BilibiliSmsLoginViewModel? smsLogin = null)
    {
        InitializeComponent();
        WindowChrome.AttachFrame(this);
        titleBinding = Bind(TitleProperty, Localization.Observe(relogin
            ? "UI.BilibiliLoginWindow.ReloginTitle"
            : "UI.BilibiliLoginWindow.AddTitle"));
        this.qrLogin = qrLogin ?? new();
        this.smsLogin = smsLogin ?? new(ShowChallengeAsync);
        this.qrLogin.SignedIn += OnSignedIn;
        this.smsLogin.SignedIn += OnSignedIn;
        this.smsLogin.PropertyChanged += SmsLogin_OnPropertyChanged;
        QrForm.DataContext = this.qrLogin;
        SmsForm.DataContext = this.smsLogin;
    }

    internal bool BrowserLoginRequested { get; private set; }

    /// <inheritdoc />
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        opened = true;
        ActivateLoginMethod();
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        opened = false;
        qrLogin.SignedIn -= OnSignedIn;
        smsLogin.SignedIn -= OnSignedIn;
        smsLogin.PropertyChanged -= SmsLogin_OnPropertyChanged;
        titleBinding.Dispose();
        qrLogin.Dispose();
        smsLogin.Dispose();
        base.OnClosed(e);
    }

    private void OnSignedIn(string cookie) => Close(cookie);

    private void SmsLogin_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!opened || LoginMethodTabs.SelectedIndex != 1 || e.PropertyName != nameof(smsLogin.Step))
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (opened && LoginMethodTabs.SelectedIndex == 1)
            {
                if (smsLogin.IsCodeStep)
                {
                    SmsCodeInput.Focus();
                }
                else if (smsLogin.IsPhoneStep)
                {
                    SmsPhoneInput.Focus();
                }
            }
        }, DispatcherPriority.Background);
    }

    private void LoginMethodTabs_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (opened && e.Source == LoginMethodTabs)
        {
            ActivateLoginMethod();
        }
    }

    private void ActivateLoginMethod()
    {
        if (LoginMethodTabs.SelectedIndex == 0)
        {
            smsLogin.Stop();
            qrLogin.RefreshQrCommand.Execute(null);
        }
        else
        {
            qrLogin.Stop();
        }
    }

    private async Task<BilibiliChallengeProof?> ShowChallengeAsync(BilibiliLoginChallenge challenge,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var window = new BilibiliCaptchaWindow(challenge);
        using var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(() => window.Close()));
        try
        {
            return await window.ShowDialog<BilibiliChallengeProof?>(this);
        }
        finally
        {
            window.Close();
        }
    }

    private async void VerifyAccount_OnClick(object? sender, RoutedEventArgs e)
    {
        if (smsLogin.VerificationUri is not { } uri)
        {
            return;
        }

        try
        {
            if (await Launcher.LaunchUriAsync(uri))
            {
                return;
            }
        }
        catch (Exception)
        {
        }

        if (opened && smsLogin.VerificationUri == uri)
        {
            smsLogin.ReportVerificationPageUnavailable();
        }
    }

    private void OpenBrowser_OnClick(object? sender, RoutedEventArgs e)
    {
        BrowserLoginRequested = true;
        Close();
    }

    private void Cancel_OnClick(object? sender, RoutedEventArgs e) => Close();
}
