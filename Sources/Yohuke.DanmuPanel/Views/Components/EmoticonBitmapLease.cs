using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed class EmoticonBitmapLease(EmoticonBitmapCacheEntry entry) : IDisposable
{
    private EmoticonBitmapCacheEntry? currentEntry = entry;

    internal Bitmap Image => currentEntry?.Image ?? throw new ObjectDisposedException(nameof(EmoticonBitmapLease));

    /// <inheritdoc />
    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess();
        var released = currentEntry;
        currentEntry = null;
        released?.ReleaseLease();
    }
}
