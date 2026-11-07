using Avalonia.Platform.Storage;

namespace Yohuke.DanmuPanel.Tests.Settings;

internal sealed class ControlledDirectoryLauncher : ILauncher
{
    internal List<Uri> Requests { get; } = [];
    internal Func<Uri, Task<bool>> Resolve { get; set; } = _ => Task.FromResult(true);

    /// <inheritdoc />
    public Task<bool> LaunchUriAsync(Uri uri)
    {
        Requests.Add(uri);
        return Resolve(uri);
    }

    /// <inheritdoc />
    public Task<bool> LaunchFileAsync(IStorageItem storageItem)
    {
        throw new InvalidOperationException("Directory launches must use an escaped file URI.");
    }
}
