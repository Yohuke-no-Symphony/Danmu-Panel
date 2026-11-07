using Avalonia.Media.Imaging;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed class EmoticonBitmapCacheEntry(string path, int decodeHeight, long fileLength,
    DateTime lastWrittenAt, Bitmap image)
{
    private int leaseCount;
    private int retainCount;
    private bool isDisposed;

    internal string Path { get; } = path;
    internal int DecodeHeight { get; } = decodeHeight;
    internal long FileLength { get; } = fileLength;
    internal DateTime LastWrittenAt { get; } = lastWrittenAt;
    internal Bitmap Image { get; } = image;
    internal long ByteSize { get; } = checked((long)image.PixelSize.Width * image.PixelSize.Height * 4);
    internal long LastCheckedAt { get; set; }

    internal EmoticonBitmapLease AcquireLease()
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        leaseCount++;
        return new(this);
    }

    internal void Retain()
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        retainCount++;
    }

    internal void ReleaseRetention()
    {
        retainCount--;
        DisposeIfUnused();
    }

    internal void ReleaseLease()
    {
        leaseCount--;
        DisposeIfUnused();
    }

    private void DisposeIfUnused()
    {
        if (!isDisposed && retainCount == 0 && leaseCount == 0)
        {
            isDisposed = true;
            Image.Dispose();
        }
    }
}
