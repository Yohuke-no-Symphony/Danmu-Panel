using Avalonia;

namespace Yohuke.DanmuPanel.Windowing;

internal interface IWindowChrome : IDisposable
{
    void ResizeClient(Size size);
}
