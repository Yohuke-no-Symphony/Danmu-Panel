using Avalonia.Media.Imaging;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed class UserAvatarThumbnail(string path, Bitmap image)
{
    private int referenceCount;

    internal string Path { get; } = path;
    internal Bitmap Image { get; } = image;
    internal long ByteSize { get; } = checked((long)image.PixelSize.Width * image.PixelSize.Height * 4);

    internal void Retain()
    {
        referenceCount++;
    }

    internal void Release()
    {
        if (--referenceCount == 0)
        {
            Image.Dispose();
        }
    }
}
