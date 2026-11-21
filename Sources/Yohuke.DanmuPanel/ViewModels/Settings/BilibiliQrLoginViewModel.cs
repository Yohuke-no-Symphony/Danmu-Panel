using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Net.Codecrete.QrCodeGenerator;
using Newtonsoft.Json;
using Yohuke.DanmuPanel.I18n;

namespace Yohuke.DanmuPanel.ViewModels.Settings;

internal sealed partial class BilibiliQrLoginViewModel : ObservableObject, IDisposable
{
    private const string LOADING = "UI.BilibiliLoginWindow.QrLoading";
    private const string EXPIRED = "UI.BilibiliLoginWindow.QrExpired";
    private readonly Func<BilibiliWebLoginClient> createClient;
    private readonly TimeSpan pollInterval;
    private string statusKey = LOADING;
    private Bitmap? qrImage;

    internal BilibiliQrLoginViewModel()
        : this(static () => new(), TimeSpan.FromSeconds(2))
    {
    }

    internal BilibiliQrLoginViewModel(Func<BilibiliWebLoginClient> createClient, TimeSpan pollInterval)
    {
        this.createClient = createClient;
        this.pollInterval = pollInterval;
        Localization.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>二维码位图。</summary>
    public Bitmap? QrImage => qrImage;

    /// <summary>当前扫码步骤的提示。</summary>
    public string StatusText => Localization.Get(statusKey);

    internal event Action<string>? SignedIn;

    internal void Stop()
    {
        RefreshQrCommand.Cancel();
        SetQrImage(null);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Stop();
        Localization.LanguageChanged -= OnLanguageChanged;
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RefreshQrAsync(CancellationToken cancellationToken)
    {
        SetQrImage(null);
        SetStatus(LOADING);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(TimeSpan.FromSeconds(25));
        string? cookie = null;

        try
        {
            using var client = createClient();
            var qr = await client.BeginQrAsync(attempt.Token);
            attempt.Token.ThrowIfCancellationRequested();
            var png = QrCode.EncodeText(qr.Url, QrCode.Ecc.Medium).ToPngBitmap(border: 4, scale: 8);
            using var stream = new MemoryStream(png);
            SetQrImage(new Bitmap(stream));
            SetStatus("UI.BilibiliLoginWindow.QrWaiting");

            attempt.CancelAfter(TimeSpan.FromMinutes(3));
            cookie = await PollForCookieAsync(client, qr.Key, attempt.Token);
            attempt.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested && QrImage is not null)
        {
            SetStatus(EXPIRED);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                SetStatus("UI.BilibiliLoginWindow.NetworkError");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or
                                          FormatException or JsonException or DataTooLongException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                SetStatus("UI.BilibiliLoginWindow.QrFailed");
            }
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                SetQrImage(null);
            }
        }

        if (cookie is not null && !attempt.IsCancellationRequested)
        {
            SignedIn?.Invoke(cookie);
        }
    }

    private async Task<string?> PollForCookieAsync(BilibiliWebLoginClient client, string key,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(pollInterval, cancellationToken);
            var result = await client.PollQrAsync(key, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            switch (result.Status)
            {
                case "Waiting":
                    SetStatus("UI.BilibiliLoginWindow.QrWaiting");
                    break;
                case "Scanned":
                    SetStatus("UI.BilibiliLoginWindow.QrScanned");
                    break;
                case "Expired":
                    SetStatus(EXPIRED);
                    return null;
                case "Complete" when result.Credentials is { } credentials:
                    return credentials.Cookie;
                case "VerificationRequired":
                    SetStatus("UI.BilibiliLoginWindow.VerificationRequired");
                    return null;
                default:
                    SetStatus("UI.BilibiliLoginWindow.QrFailed");
                    return null;
            }
        }
    }

    private void SetQrImage(Bitmap? image)
    {
        var previous = qrImage;
        SetProperty(ref qrImage, image, nameof(QrImage));
        previous?.Dispose();
    }

    private void SetStatus(string key) => SetProperty(ref statusKey, key, nameof(StatusText));

    private void OnLanguageChanged(object? sender, EventArgs e) => OnPropertyChanged(nameof(StatusText));
}
