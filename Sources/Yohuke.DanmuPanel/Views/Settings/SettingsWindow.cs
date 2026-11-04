using Avalonia.Controls;
using Avalonia.Threading;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Windowing;

namespace Yohuke.DanmuPanel.Views.Settings;

/// <summary>关闭前完成设置保存的独立设置窗口。</summary>
public sealed class SettingsWindow : Window
{
    private readonly SettingsView view;
    private bool closeApproved;
    private bool closing;

    public event Action<bool>? ClearRequested;

    /// <summary>转发设置中的即时菜单位置切换。</summary>
    public event Action<bool>? WindowMenuOnMacChanged
    {
        add => view.WindowMenuOnMacChanged += value;
        remove => view.WindowMenuOnMacChanged -= value;
    }

    /// <summary>请求由应用在当前窗口下显示手动更新检查。</summary>
    public event Action? CheckForUpdatesRequested
    {
        add => view.CheckForUpdatesRequested += value;
        remove => view.CheckForUpdatesRequested -= value;
    }

    /// <summary>转发启动更新检查偏好的即时变化。</summary>
    public event Action<bool>? StartupUpdatePreferenceChanged
    {
        add => view.StartupUpdatePreferenceChanged += value;
        remove => view.StartupUpdatePreferenceChanged -= value;
    }

    /// <summary>创建设置窗口及账号页容器。</summary>
    public SettingsWindow(PanelRuntime runtime, Control accountsView)
        : this(runtime, accountsView, new SettingsTransferDialogs())
    {
    }

    internal SettingsWindow(PanelRuntime runtime, Control accountsView, ISettingsTransferDialogs transferDialogs)
    {
        Width = 1020;
        Height = 760;
        MinWidth = 860;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Bind(TitleProperty, Localization.Observe("Settings.Title"));
        view = new(runtime, accountsView, OperatingSystem.IsMacOS(), transferDialogs);
        view.ClearRequested += async all =>
        {
            if (await view.SaveAsync())
            {
                await Dispatcher.UIThread.InvokeAsync(() => ClearRequested?.Invoke(all), DispatcherPriority.Background);
            }
        };
        view.Saved += () =>
        {
            closeApproved = true;
            Close();
        };
        Content = view;
        WindowChrome.AttachFrame(this);
        Closing += OnClosing;
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (closeApproved)
        {
            return;
        }

        args.Cancel = true;
        if (closing)
        {
            return;
        }

        closing = true;
        try
        {
            await view.SaveAsync();
        }
        finally
        {
            closing = false;
        }
    }
}
