using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Newtonsoft.Json;
using Yohuke.DanmuPanel.I18n;

namespace Yohuke.DanmuPanel.ViewModels.Settings;

internal sealed partial class BilibiliSmsLoginViewModel : ObservableObject, IDisposable
{
    private const int RESEND_SECONDS = 60;
    private static readonly TimeSpan REQUEST_TIMEOUT = TimeSpan.FromSeconds(25);
    private readonly Func<BilibiliWebLoginClient> createClient;
    private readonly Func<BilibiliLoginChallenge, CancellationToken, Task<BilibiliChallengeProof?>> showChallenge;
    private readonly TimeProvider clock;
    private readonly DispatcherTimer cooldownTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private BilibiliWebLoginClient? client;
    private (string CountryCode, string Phone)? sessionPhone;
    private BilibiliLoginChallenge? challenge;
    private string? captchaKey;
    private long? sentTimestamp;
    private string? statusKey;

    [ObservableProperty]
    private string countryCode = "86";

    [ObservableProperty]
    private string phone = "";

    [ObservableProperty]
    private string code = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RequestSmsCommand))]
    [NotifyCanExecuteChangedFor(nameof(CompleteChallengeCommand))]
    [NotifyCanExecuteChangedFor(nameof(CompleteSmsCommand))]
    private bool isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RequestSmsCommand))]
    [NotifyCanExecuteChangedFor(nameof(CompleteChallengeCommand))]
    [NotifyCanExecuteChangedFor(nameof(CompleteSmsCommand))]
    [NotifyPropertyChangedFor(nameof(IsPhoneStep), nameof(IsChallengeStep), nameof(IsCodeStep), nameof(RequestButtonText))]
    private BilibiliSmsLoginStep step;

    [ObservableProperty]
    private Uri? verificationUri;

    internal BilibiliSmsLoginViewModel(
        Func<BilibiliLoginChallenge, CancellationToken, Task<BilibiliChallengeProof?>> showChallenge)
        : this(static () => new(), showChallenge, TimeProvider.System)
    {
    }

    internal BilibiliSmsLoginViewModel(Func<BilibiliWebLoginClient> createClient,
        Func<BilibiliLoginChallenge, CancellationToken, Task<BilibiliChallengeProof?>> showChallenge,
        TimeProvider clock)
    {
        this.createClient = createClient;
        this.showChallenge = showChallenge;
        this.clock = clock;
        cooldownTimer.Tick += OnCooldownTick;
        Localization.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>当前短信登录步骤的提示。</summary>
    public string StatusText => statusKey is { } key ? Localization.Get(key) : "";

    /// <summary>是否有当前需要显示的进度或错误。</summary>
    public bool HasStatus => statusKey is not null;

    /// <summary>是否正在填写手机号。</summary>
    public bool IsPhoneStep => Step == BilibiliSmsLoginStep.Phone;

    /// <summary>是否已获取人机验证。</summary>
    public bool IsChallengeStep => Step == BilibiliSmsLoginStep.Challenge;

    /// <summary>是否已发送短信验证码。</summary>
    public bool IsCodeStep => Step == BilibiliSmsLoginStep.Code;

    /// <summary>发码、重发或倒计时按钮文本。</summary>
    public string RequestButtonText => RemainingSeconds > 0
        ? Localization.Format("UI.BilibiliLoginWindow.SmsResendCountdown", RemainingSeconds)
        : Localization.Get(IsCodeStep ? "UI.BilibiliLoginWindow.SmsResend" : "UI.BilibiliLoginWindow.SmsRequest");

    private int RemainingSeconds => sentTimestamp is { } sent
        ? Math.Max(0, (int)Math.Ceiling(RESEND_SECONDS - clock.GetElapsedTime(sent).TotalSeconds))
        : 0;

    internal event Action<string>? SignedIn;

    internal void ReportVerificationPageUnavailable() => SetStatus("UI.BilibiliLoginWindow.VerificationPageUnavailable");

    internal void Stop()
    {
        RequestSmsCommand.Cancel();
        CompleteChallengeCommand.Cancel();
        CompleteSmsCommand.Cancel();
        ClearSmsSession();
        VerificationUri = null;
        Step = BilibiliSmsLoginStep.Phone;
        SetStatus(null);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Stop();
        Phone = "";
        cooldownTimer.Stop();
        cooldownTimer.Tick -= OnCooldownTick;
        Localization.LanguageChanged -= OnLanguageChanged;
    }

    private bool CanRequestSms() => !IsBusy && RemainingSeconds == 0 && (IsPhoneStep || IsCodeStep);

    [RelayCommand(CanExecute = nameof(CanRequestSms))]
    private async Task RequestSmsAsync(CancellationToken cancellationToken)
    {
        if (!CanRequestSms())
        {
            return;
        }

        var cid = (CountryCode?.Trim() ?? "").TrimStart('+');
        var tel = Phone?.Trim() ?? "";
        if (cid.Length is < 1 or > 5 || !cid.All(char.IsAsciiDigit) ||
            tel.Length is < 5 or > 20 || !tel.All(char.IsAsciiDigit))
        {
            SetStatus("UI.BilibiliLoginWindow.SmsPhoneInvalid");
            return;
        }

        VerificationUri = null;
        IsBusy = true;
        SetStatus("UI.BilibiliLoginWindow.SmsPreparingChallenge");
        BilibiliWebLoginClient? pendingClient = null;

        try
        {
            pendingClient = createClient();
            using var request = StartRequest(cancellationToken);
            var nextChallenge = await pendingClient.GetChallengeAsync(request.Token);
            request.Token.ThrowIfCancellationRequested();
            ClearSmsSession();
            client = pendingClient;
            pendingClient = null;
            sessionPhone = (cid, tel);
            challenge = nextChallenge;
            Step = BilibiliSmsLoginStep.Challenge;
            SetStatus(null);
        }
        catch (Exception exception) when (IsLoginError(exception))
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                SetStatus(IsNetworkError(exception)
                    ? "UI.BilibiliLoginWindow.NetworkError"
                    : "UI.BilibiliLoginWindow.SmsChallengeFailed");
            }
        }
        finally
        {
            pendingClient?.Dispose();
            IsBusy = false;
        }
    }

    private bool CanCompleteChallenge() => !IsBusy && IsChallengeStep;

    [RelayCommand(CanExecute = nameof(CanCompleteChallenge))]
    private async Task CompleteChallengeAsync(CancellationToken cancellationToken)
    {
        if (!CanCompleteChallenge() || client is not { } activeClient || sessionPhone is not { } number ||
            challenge is not { } currentChallenge)
        {
            return;
        }

        IsBusy = true;
        var submitting = false;
        try
        {
            SetStatus("UI.BilibiliLoginWindow.SmsCompleteChallenge");
            var proof = await showChallenge(currentChallenge, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (proof is null)
            {
                SetStatus(null);
                return;
            }

            submitting = true;
            SetStatus("UI.BilibiliLoginWindow.SmsSending");
            sentTimestamp = clock.GetTimestamp();
            OnCooldownTick(null, EventArgs.Empty);
            cooldownTimer.Start();
            using var send = StartRequest(cancellationToken);
            var key = await activeClient.SendSmsAsync(number.CountryCode, number.Phone, currentChallenge, proof, send.Token);
            send.Token.ThrowIfCancellationRequested();

            challenge = null;
            captchaKey = key;
            Step = BilibiliSmsLoginStep.Code;
            SetStatus(null);
        }
        catch (Exception exception) when (IsLoginError(exception))
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                if (submitting)
                {
                    ClearSmsSession();
                    Step = BilibiliSmsLoginStep.Phone;
                }

                var failureKey = submitting
                    ? "UI.BilibiliLoginWindow.SmsSendFailed"
                    : "UI.BilibiliLoginWindow.SmsChallengeFailed";
                SetStatus(IsNetworkError(exception) ? "UI.BilibiliLoginWindow.NetworkError" : failureKey);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanCompleteSms() => !IsBusy && IsCodeStep;

    [RelayCommand(CanExecute = nameof(CanCompleteSms))]
    private async Task CompleteSmsAsync(CancellationToken cancellationToken)
    {
        if (!CanCompleteSms() || client is not { } activeClient || sessionPhone is not { } number || captchaKey is not { } key)
        {
            return;
        }

        var verificationCode = Code?.Trim() ?? "";
        if (verificationCode.Length != 6 || !verificationCode.All(char.IsAsciiDigit))
        {
            SetStatus("UI.BilibiliLoginWindow.SmsCodeInvalid");
            return;
        }

        IsBusy = true;
        SetStatus("UI.BilibiliLoginWindow.SmsSigningIn");
        string? cookie = null;
        try
        {
            using var request = StartRequest(cancellationToken);
            var result = await activeClient.CompleteSmsAsync(number.CountryCode, number.Phone, key, verificationCode,
                request.Token);
            request.Token.ThrowIfCancellationRequested();
            switch (result.Status)
            {
                case "Complete" when result.Credentials is { } credentials:
                    cookie = credentials.Cookie;
                    break;
                case "VerificationRequired":
                    ClearSmsSession();
                    VerificationUri = Uri.TryCreate(result.VerificationUrl, UriKind.Absolute, out var uri) ? uri : null;
                    Step = BilibiliSmsLoginStep.Verification;
                    SetStatus("UI.BilibiliLoginWindow.VerificationRequired");
                    break;
                default:
                    SetStatus("UI.BilibiliLoginWindow.SmsCodeFailed");
                    break;
            }
        }
        catch (Exception exception) when (IsLoginError(exception))
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                SetStatus(IsNetworkError(exception)
                    ? "UI.BilibiliLoginWindow.NetworkError"
                    : "UI.BilibiliLoginWindow.SmsCodeFailed");
            }
        }
        finally
        {
            IsBusy = false;
        }

        if (cookie is not null && !cancellationToken.IsCancellationRequested)
        {
            ClearSmsSession();
            Step = BilibiliSmsLoginStep.Phone;
            SignedIn?.Invoke(cookie);
        }
    }

    partial void OnCountryCodeChanged(string value) => Stop();

    partial void OnPhoneChanged(string value) => Stop();

    [RelayCommand]
    private void ChangePhone() => Stop();

    private void ClearSmsSession()
    {
        client?.Dispose();
        client = null;
        sessionPhone = null;
        challenge = null;
        captchaKey = null;
        Code = "";
    }

    private void OnCooldownTick(object? sender, EventArgs e)
    {
        if (RemainingSeconds == 0)
        {
            cooldownTimer.Stop();
            RequestSmsCommand.NotifyCanExecuteChanged();
        }

        OnPropertyChanged(nameof(RequestButtonText));
    }

    private void SetStatus(string? key)
    {
        if (SetProperty(ref statusKey, key, nameof(StatusText)))
        {
            OnPropertyChanged(nameof(HasStatus));
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(RequestButtonText));
    }

    private static CancellationTokenSource StartRequest(CancellationToken token)
    {
        var request = CancellationTokenSource.CreateLinkedTokenSource(token);
        request.CancelAfter(REQUEST_TIMEOUT);
        return request;
    }

    private static bool IsLoginError(Exception exception) =>
        IsNetworkError(exception) || exception is ArgumentException or InvalidOperationException or
            InvalidDataException or FormatException or JsonException or NotSupportedException;

    private static bool IsNetworkError(Exception exception) =>
        exception is HttpRequestException or IOException or OperationCanceledException;
}
