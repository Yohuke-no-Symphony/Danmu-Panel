using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Views.Components;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Platform;

namespace Yohuke.DanmuPanel.Windowing;

/// <summary>为应用窗口安装跨平台自定义标题栏。</summary>
public static class WindowChrome
{
    /// <summary>安装共享窗口标题栏并管理原生窗口边框生命周期。</summary>
    public static WindowTitleBar AttachFrame(Window window, Control? rightContent = null)
    {
        var frame = new WindowFrameSurface { RightContent = rightContent };
        var titleBar = frame.TitleBar;
        var titleBinding = titleBar.Bind(WindowTitleBar.TitleProperty,
            new Binding(nameof(Window.Title)) { Source = window });
        var content = window.Content;
        window.Content = null;
        frame.BodyContent = content;
        window.Content = frame;
        window.ExtendClientAreaToDecorationsHint = true;
        window.ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.PreferSystemChrome;
        window.ExtendClientAreaTitleBarHeightHint = titleBar.Height;
        var chrome = Attach(window, titleBar);
        window.Closed += OnClosed;
        return titleBar;

        void OnClosed(object? sender, EventArgs e)
        {
            window.Closed -= OnClosed;
            chrome.Dispose();
            titleBinding.Dispose();
        }
    }

    internal static IWindowChrome Attach(Window window, IWindowTitleBar titleBar)
    {
        window.SystemDecorations = SystemDecorations.Full;
        if (OperatingSystem.IsWindows() && window.TryGetPlatformHandle() is { HandleDescriptor: "HWND" })
        {
            return new WindowsWindowChrome(window, titleBar);
        }

        if (OperatingSystem.IsMacOS() && window.TryGetPlatformHandle() is IPlatformHandle { HandleDescriptor: "NSWindow", Handle: not 0 })
        {
            return new MacOsWindowChrome(window, titleBar);
        }

        return new ManagedWindowChrome(window);
    }

    internal static void ValidateClientSize(Size size)
    {
        if (!double.IsFinite(size.Width) || !double.IsFinite(size.Height) || size.Width <= 0 || size.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), Localization.Get("Window.Chrome.InvalidClientSize"));
        }
    }
}
