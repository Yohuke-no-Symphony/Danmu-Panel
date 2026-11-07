using Avalonia.Media.Imaging;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed record EmoticonBitmapLoadResult(long FileLength, DateTime LastWrittenAt, Bitmap? Image);
