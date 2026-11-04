using System.Net;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Models.Updates;
using Yohuke.DanmuPanel.Services.Updates;

namespace Yohuke.DanmuPanel.ViewModels.Updates;

/// <summary>在 UI 线程合并更新检查，管理手动反馈、自动偏好和可等待的取消。</summary>
public sealed class AppUpdateViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IAppUpdateService service;
    private readonly PanelSettings settings;
    private readonly Version currentVersion;
    private readonly Func<Uri, Task<bool>> launchUri;
    private readonly Action<Exception> reportFailure;
    private readonly HashSet<AppUpdateRequest> requests = [];
    private AppUpdateRequest? pending;
    private Task? startupCheck;
    private Task? disposal;
    private string messageKey = "Updates.Checking";
    private bool manualPresentation;
    private bool disposed;
    private bool failed;
    private bool isChecking;
    private bool isDialogOpen;
    private bool hasBrowserError;
    private AppUpdateInfo? availableUpdate;

    /// <summary>连接检查服务及应用边界，不自动发起请求或接管 HTTP 客户端。</summary>
    public AppUpdateViewModel(IAppUpdateService service, PanelSettings settings, Version currentVersion,
        Func<Uri, Task<bool>> launchUri, Action<Exception> reportFailure)
    {
        this.service = service;
        this.settings = settings;
        this.currentVersion = currentVersion;
        this.launchUri = launchUri;
        this.reportFailure = reportFailure;
        RetryCommand = new(() => StartCheck(true), () => CanRetry);
        OpenReleaseCommand = new(OpenReleaseAsync, () => HasUpdate && !disposed);
        Localization.LanguageChanged += OnLanguageChanged;
    }

    public string CurrentVersion => currentVersion.ToString();
    public string Message => Localization.Get(messageKey);
    public string AvailableVersion => availableUpdate is { } release
        ? Localization.Format("Updates.AvailableVersion", release.TagName) : string.Empty;
    public string ReleaseNotes => availableUpdate?.ReleaseNotes ?? string.Empty;
    public bool HasUpdate => availableUpdate is not null;
    public bool HasReleaseNotes => HasUpdate && !string.IsNullOrWhiteSpace(ReleaseNotes);
    public bool IsChecking => isChecking;
    public bool IsDialogOpen => isDialogOpen;
    public bool CanRetry => failed && !isChecking && !disposed;
    public bool HasBrowserError => hasBrowserError;
    public AsyncRelayCommand RetryCommand { get; }
    public AsyncRelayCommand OpenReleaseCommand { get; }

    /// <summary>应用启动后至多执行一次后台检查，禁用时不访问发布源。</summary>
    public Task CheckOnStartupAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        return startupCheck ??= disposed || !settings.CheckForUpdatesOnStartup
            ? Task.CompletedTask : StartCheck(false);
    }

    /// <summary>立即显示检查中，并加入现有请求或开始新的手动检查。</summary>
    public Task CheckManuallyAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        return StartCheck(true);
    }

    /// <summary>应用启动检查偏好，关闭时取消尚未升级为手动检查的自动请求。</summary>
    public void SetStartupCheckEnabled(bool enabled)
    {
        Dispatcher.UIThread.VerifyAccess();
        settings.CheckForUpdatesOnStartup = enabled;
        if (!enabled && !manualPresentation)
        {
            Dismiss();
        }
    }

    /// <summary>关闭本次提示并取消检查；已取消请求的迟到响应不会重新打开提示。</summary>
    public void Dismiss()
    {
        Dispatcher.UIThread.VerifyAccess();
        var request = pending;
        pending = null;
        isDialogOpen = false;
        isChecking = false;
        availableUpdate = null;
        hasBrowserError = false;
        failed = false;
        request?.Cancellation.Cancel();
        NotifyState();
    }

    /// <summary>取消并等待本轮所有检查，不释放功能，允许退出保存失败后继续使用。</summary>
    public Task CancelPendingAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        Dismiss();
        return Task.WhenAll(requests.Select(request => request.Completion.Task).ToArray());
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        return new(disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        disposed = true;
        Localization.LanguageChanged -= OnLanguageChanged;
        await CancelPendingAsync();
    }

    private Task StartCheck(bool manual)
    {
        if (disposed)
        {
            return Task.CompletedTask;
        }
        if (pending is { } existing)
        {
            existing.IsManual |= manual;
            manualPresentation |= manual;
            isDialogOpen |= manual;
            NotifyState();
            return existing.Completion.Task;
        }

        var request = new AppUpdateRequest(manual);
        pending = request;
        requests.Add(request);
        manualPresentation = manual;
        availableUpdate = null;
        hasBrowserError = false;
        failed = false;
        isChecking = true;
        isDialogOpen = manual;
        messageKey = "Updates.Checking";
        NotifyState();
        _ = RunCheckAsync(request);
        return request.Completion.Task;
    }

    private async Task RunCheckAsync(AppUpdateRequest request)
    {
        try
        {
            var result = await service.CheckAsync(currentVersion, request.Cancellation.Token);
            if (!Owns(request))
            {
                return;
            }
            availableUpdate = result.Status == AppUpdateCheckStatus.UPDATE_AVAILABLE ? result.Release : null;
            messageKey = result.Status switch
            {
                AppUpdateCheckStatus.UPDATE_AVAILABLE => "Updates.Available",
                AppUpdateCheckStatus.UP_TO_DATE => "Updates.UpToDate",
                _ => "Updates.NoRelease"
            };
            isDialogOpen = request.IsManual || HasUpdate && settings.CheckForUpdatesOnStartup;
        }
        catch (OperationCanceledException) when (request.Cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (Owns(request))
            {
                failed = true;
                messageKey = exception switch
                {
                    TimeoutException => "Updates.TimedOut",
                    HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests }
                        => "Updates.RateLimited",
                    InvalidDataException => "Updates.InvalidRelease",
                    _ => "Updates.CheckFailed"
                };
                isDialogOpen = request.IsManual;
                reportFailure(exception);
            }
        }
        finally
        {
            if (ReferenceEquals(pending, request))
            {
                pending = null;
                isChecking = false;
                NotifyState();
            }
            requests.Remove(request);
            request.Cancellation.Dispose();
            request.Completion.TrySetResult();
        }
    }

    private bool Owns(AppUpdateRequest request)
    {
        return !disposed && ReferenceEquals(pending, request) && !request.Cancellation.IsCancellationRequested;
    }

    private async Task OpenReleaseAsync()
    {
        if (availableUpdate is not { } release || disposed)
        {
            return;
        }
        var success = false;
        try
        {
            success = await launchUri(release.ReleaseUri);
        }
        catch (Exception exception)
        {
            reportFailure(exception);
        }
        if (!disposed && ReferenceEquals(availableUpdate, release) && isDialogOpen)
        {
            hasBrowserError = !success;
            NotifyState();
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        NotifyState();
    }

    private void NotifyState()
    {
        OnPropertyChanged(string.Empty);
        RetryCommand.NotifyCanExecuteChanged();
        OpenReleaseCommand.NotifyCanExecuteChanged();
    }
}
