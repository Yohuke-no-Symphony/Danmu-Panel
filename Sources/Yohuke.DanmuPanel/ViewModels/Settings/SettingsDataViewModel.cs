using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yohuke.DanmuPanel.I18n;

namespace Yohuke.DanmuPanel.ViewModels.Settings;

/// <summary>管理数据目录入口与后台缓存大小统计的显示状态。</summary>
public sealed partial class SettingsDataViewModel : ObservableObject
{
    private static string[] SizeUnits { get; } = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];
    private readonly Func<CancellationToken, Task<long>> measureCacheSize;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CacheSizeText))]
    private long? cacheSizeBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CacheSizeText))]
    private bool isCalculatingCacheSize;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CacheSizeText))]
    private bool cacheSizeFailed;

    public string DataDirectoryPath { get; }
    public IAsyncRelayCommand OpenDataDirectoryCommand { get; }
    public string CacheSizeText => IsCalculatingCacheSize
        ? Localization.Get("Settings.CacheSizeCalculating")
        : CacheSizeFailed
            ? Localization.Get("Settings.CacheSizeUnavailable")
            : CacheSizeBytes is { } size ? FormatSize(size) : "—";

    /// <summary>注入缓存统计与宿主目录操作，保持状态独立于平台控件。</summary>
    public SettingsDataViewModel(string dataDirectoryPath, Func<CancellationToken, Task<long>> measureCacheSize,
        Func<Task> openDataDirectory)
    {
        DataDirectoryPath = dataDirectoryPath;
        this.measureCacheSize = measureCacheSize;
        OpenDataDirectoryCommand = new AsyncRelayCommand(openDataDirectory);
    }

    /// <summary>重新读取统计状态的本地化文案。</summary>
    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(CacheSizeText));
    }

    [RelayCommand]
    private async Task RefreshCacheSizeAsync(CancellationToken cancellationToken)
    {
        IsCalculatingCacheSize = true;
        CacheSizeFailed = false;
        try
        {
            CacheSizeBytes = await measureCacheSize(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 取消保留上次统计结果，不把主动取消显示为读取失败。
        }
        catch (Exception)
        {
            CacheSizeBytes = null;
            CacheSizeFailed = true;
        }
        finally
        {
            IsCalculatingCacheSize = false;
        }
    }

    private static string FormatSize(long bytes)
    {
        var size = (double)bytes;
        var unit = 0;
        while (size >= 1024 && unit < SizeUnits.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return size.ToString("0.##", CultureInfo.CurrentCulture) + " " + SizeUnits[unit];
    }
}
