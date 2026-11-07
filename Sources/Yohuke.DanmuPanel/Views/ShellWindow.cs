using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Layouts;
using Yohuke.DanmuPanel.Menus;
using Yohuke.DanmuPanel.Views.About;
using Yohuke.DanmuPanel.Views.Components;
using Yohuke.DanmuPanel.Windowing;

namespace Yohuke.DanmuPanel.Views;

/// <summary>承载阅读面板和测试面板、原生窗口边框及布局持久化的桌面工作台。</summary>
public sealed class ShellWindow : Window
{
    private readonly IReadOnlyDictionary<string, Control> panels;
    private readonly WorkspaceLayoutStore layoutStore;
    private readonly ShellWindowRegistry windows;
    private readonly ContentControl dockHost;
    private readonly Menu menu;
    private readonly WorkbenchMenuCatalog menuCatalog;
    private readonly WorkbenchMenuIcons menuIcons = new();
    private readonly Dictionary<Window, WorkbenchMenuProjection> menuProjections = [];
    private readonly bool supportsNativeMenus;
    private WorkbenchLayoutController? layouts;
    private Task? initialization;
    private Task? closing;
    private bool canClose;
    private bool useWindowMenuOnMac;

    /// <summary>创建工作台；面板键为 workspace、danmu、superChat、gift、guard、testMessages、log。</summary>
    public ShellWindow(IReadOnlyDictionary<string, Control> panels, Control roomToolbar, string configDirectory)
        : this(panels, roomToolbar, configDirectory, OperatingSystem.IsMacOS())
    {
    }

    internal ShellWindow(IReadOnlyDictionary<string, Control> panels, Control roomToolbar, string configDirectory,
        bool supportsNativeMenus)
    {
        ArgumentNullException.ThrowIfNull(panels);
        ArgumentNullException.ThrowIfNull(roomToolbar);
        if (!panels.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(WorkbenchPanelIds.All))
        {
            throw new ArgumentException(Localization.Get("Layout.UniquePanelViewsRequired"), nameof(panels));
        }

        this.panels = panels;
        this.supportsNativeMenus = supportsNativeMenus;
        layoutStore = new(configDirectory);
        windows = new(this);
        Title = "Yohuke Danmu Panel";
        Width = 1280;
        Height = 820;
        MinWidth = 800;
        MinHeight = 520;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.PreferSystemChrome;
        ExtendClientAreaTitleBarHeightHint = WindowTitleBar.DEFAULT_HEIGHT;
        if (AssetLoader.Exists(new("avares://Yohuke.DanmuPanel/Assets/favicon.ico")))
        {
            Icon = new WindowIcon(AssetLoader.Open(new("avares://Yohuke.DanmuPanel/Assets/favicon.ico")));
        }

        var header = new ShellHeaderSurface { RoomToolbar = roomToolbar };
        menu = header.Menu;
        menu.IsVisible = !supportsNativeMenus;
        menuCatalog = new(() => SettingsRequested?.Invoke(), () => _ = ShowAboutAsync(), () => ClearRoomRequested?.Invoke(),
            () => ClearAllRequested?.Invoke(), () => _ = RequestCloseAsync(), () => _ = SaveLayoutAsAsync(),
            () => windows.IsInteractionBlocked, ReportFailure);
        windows.InteractionChanged += menuCatalog.Refresh;
        AttachMenu(this);
        var pinButton = new WindowPinButton { Command = new RelayCommand(() => Topmost = !Topmost) };
        pinButton.Bind(WindowPinButton.IsPinnedProperty, this.GetObservable(TopmostProperty));
        dockHost = new ShellSurface();
        Content = dockHost;
        var titleBar = WindowChrome.AttachFrame(this, pinButton);
        titleBar.HeaderContent = header;
        Opened += OnOpened;
        Closed += OnClosed;
    }

    /// <summary>请求打开设置窗口。</summary>
    public event Action? SettingsRequested;

    /// <summary>请求由应用确认并清空当前房间记录。</summary>
    public event Action? ClearRoomRequested;

    /// <summary>请求由应用确认并清空全部记录。</summary>
    public event Action? ClearAllRequested;

    /// <summary>报告布局、关闭或菜单操作失败。</summary>
    public event Action<Exception>? OperationFailed;

    /// <summary>关闭前完成应用数据落盘及监听资源释放；失败时保留窗口。</summary>
    public Func<Task>? BeforeCloseAsync { get; set; }

    /// <summary>获取当前窗口是否已完成关闭前持久化。</summary>
    public bool CanClose => canClose;

    internal bool IsInteractionBlocked => windows.IsInteractionBlocked;
    internal RelayCommand AboutCommand => menuCatalog.AboutCommand;

    internal event Action? InteractionChanged
    {
        add => windows.InteractionChanged += value;
        remove => windows.InteractionChanged -= value;
    }

    /// <summary>在 macOS 上即时切换主窗口内菜单，并同步所有业务窗口的原生菜单显隐。</summary>
    public bool UseWindowMenuOnMac
    {
        get => useWindowMenuOnMac;
        set
        {
            Dispatcher.UIThread.VerifyAccess();
            if (useWindowMenuOnMac == value)
            {
                return;
            }

            useWindowMenuOnMac = value;
            UpdateMenuMode();
        }
    }

    /// <summary>加载并恢复共享布局，可在显示窗口前等待完成。</summary>
    public Task InitializeLayoutAsync()
    {
        return initialization ??= InitializeCoreAsync();
    }

    /// <summary>显示或激活指定面板。</summary>
    public void ShowPanel(string panelId)
    {
        RequireLayouts().ShowPanel(panelId);
    }

    /// <summary>将指定面板移动到独立浮窗。</summary>
    public void FloatPanel(string panelId)
    {
        RequireLayouts().FloatPanel(panelId);
    }

    /// <summary>显示模态窗口并暂停业务浮窗交互。</summary>
    public Task<T> ShowModalAsync<T>(Window dialog)
    {
        return windows.ShowDialogAsync<T>(dialog);
    }

    /// <summary>保存布局及应用状态后关闭，失败时允许重试。</summary>
    public Task CloseAsync()
    {
        if (closing is not null)
        {
            return closing;
        }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        closing = completion.Task;
        _ = CompleteCloseAsync(completion);
        return completion.Task;
    }

    internal WorkbenchLayoutController Layouts => RequireLayouts();

    /// <inheritdoc />
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (!canClose && !e.Cancel)
        {
            e.Cancel = true;
            _ = RequestCloseAsync();
        }
    }

    private async Task InitializeCoreAsync()
    {
        var state = await layoutStore.LoadAsync();
        layouts = new(this, panels, state, layoutStore, RegisterFloatingWindow, UnregisterFloatingWindow);
        layouts.PersistenceFailed += ReportFailure;
        dockHost.Content = layouts.Host;
        menuCatalog.AttachLayouts(layouts);
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        try
        {
            await InitializeLayoutAsync();
        }
        catch (Exception exception)
        {
            ReportFailure(exception);
        }
    }

    private async Task RequestCloseAsync()
    {
        try
        {
            await CloseAsync();
        }
        catch (Exception exception)
        {
            ReportFailure(exception);
        }
    }

    private async Task CompleteCloseAsync(TaskCompletionSource completion)
    {
        try
        {
            await CloseCoreAsync();
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            closing = null;
            completion.TrySetException(exception);
        }
    }

    private async Task CloseCoreAsync()
    {
        IsEnabled = false;
        windows.SetShutdownPending(true);
        try
        {
            if (initialization is not null)
            {
                try
                {
                    await initialization;
                }
                catch when (layouts is null)
                {
                }
            }

            if (layouts is not null)
            {
                await layouts.FlushAsync();
            }

            if (BeforeCloseAsync is { } beforeClose)
            {
                await beforeClose();
            }

            if (layouts is not null)
            {
                await layouts.DisposeAsync();
            }

            canClose = true;
            Close();
        }
        catch
        {
            closing = null;
            IsEnabled = true;
            windows.SetShutdownPending(false);
            throw;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        Closed -= OnClosed;
        if (layouts is not null)
        {
            layouts.PersistenceFailed -= ReportFailure;
        }
        windows.InteractionChanged -= menuCatalog.Refresh;
        foreach (var window in menuProjections.Keys.ToArray())
        {
            DetachMenu(window);
        }
        menuCatalog.Dispose();
        windows.Dispose();
        Dispatcher.UIThread.Post(menuIcons.Dispose, DispatcherPriority.Background);
    }

    private void RegisterFloatingWindow(Window window)
    {
        windows.Register(window);
        AttachMenu(window);
    }

    private void UnregisterFloatingWindow(Window window)
    {
        DetachMenu(window);
        windows.Unregister(window);
    }

    private void AttachMenu(Window window)
    {
        var projection = new WorkbenchMenuProjection(menuCatalog, menuIcons, window == this ? menu : null);
        menuProjections.Add(window, projection);
        projection.SetNativeMenuVisible(supportsNativeMenus && !UseWindowMenuOnMac);

        if (supportsNativeMenus)
        {
            NativeMenu.SetMenu(window, projection.NativeMenu);
        }
    }

    private void DetachMenu(Window window)
    {
        if (menuProjections.Remove(window, out var projection))
        {
            projection.Dispose();
        }
    }

    private void UpdateMenuMode()
    {
        var useNative = supportsNativeMenus && !UseWindowMenuOnMac;
        menu.IsVisible = !useNative;
        foreach (var projection in menuProjections.Values)
        {
            projection.SetNativeMenuVisible(useNative);
        }
    }


    private async Task ShowAboutAsync()
    {
        try
        {
            await ShowModalAsync<object?>(new AboutWindow());
        }
        catch (Exception exception)
        {
            ReportFailure(exception);
        }
    }

    private async Task SaveLayoutAsAsync()
    {
        try
        {
            var name = await ShowModalAsync<string?>(new ShellLayoutNameWindow());
            if (name is not null)
            {
                RequireLayouts().SaveAs(name);
            }
        }
        catch (Exception exception)
        {
            ReportFailure(exception);
        }
    }

    private WorkbenchLayoutController RequireLayouts()
    {
        return layouts ?? throw new InvalidOperationException(Localization.Get("UI.MainWindow.RestoringLayout"));
    }

    private void ReportFailure(Exception exception)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ReportFailure(exception));
            return;
        }
        OperationFailed?.Invoke(exception);
    }
}
