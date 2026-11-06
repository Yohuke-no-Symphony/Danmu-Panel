using System.Net;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Tests.Workspace;
using Yohuke.DanmuPanel.Views.Components;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Windowing;

/// <summary>通过真实服务和解码器验证损坏头像修复与重试上限。</summary>
public sealed class UserAvatarRecoveryTests
{
    private const string AVATAR_URL = "https://example.com/avatar.png";
    private static readonly byte[] PNG = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAEUlEQVR4nGMI6LnzH4QZYAwAXRQK3dFXl4QAAAAASUVORK5CYII=");
    private static readonly byte[] BROKEN_PNG = [137, 80, 78, 71, 13, 10, 26, 10];

    /// <summary>带合法文件头的截断缓存会被真实控件识别并从网络重取。</summary>
    [AvaloniaFact]
    public async Task BrokenDiskAvatarIsReplacedWithDecodableDownload()
    {
        using var directory = new MessageStoreTestDirectory();
        var directories = new AppDirectoryService(directory.Path);
        Directory.CreateDirectory(directories.AvatarCacheDirectory);
        var downloads = 0;
        using var http = new HttpClient(new AvatarHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref downloads);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PNG) });
        }));
        using var images = new AvatarCacheService(directories, http);
        var path = images.GetAvatarFilePath("user-1", AVATAR_URL);
        await File.WriteAllBytesAsync(path, BROKEN_PNG);
        var source = new DelegateUserAvatarSource((_, _) => Task.FromResult(AVATAR_URL));
        await using var avatars = new UserAvatarService(directories, images, source);
        var avatar = new UserAvatarView(avatars) { UserUid = 1, Width = 20, Height = 20 };
        var window = new Window { Content = avatar, Width = 200, Height = 100 };
        try
        {
            window.Show();
            await WaitForIdleAsync(avatar, window);

            Assert.NotNull(avatar.AvatarImage);
            Assert.Equal(PNG, await File.ReadAllBytesAsync(path));
            Assert.Equal(1, downloads);
            Assert.Equal(2, source.Calls);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>服务器连续返回损坏图片时，更新通知不会造成无限下载。</summary>
    [AvaloniaFact]
    public async Task RepeatedBrokenDownloadsStopAfterSingleImmediateRetry()
    {
        using var directory = new MessageStoreTestDirectory();
        var directories = new AppDirectoryService(directory.Path);
        var downloads = 0;
        using var http = new HttpClient(new AvatarHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref downloads);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(BROKEN_PNG) });
        }));
        using var images = new AvatarCacheService(directories, http);
        var source = new DelegateUserAvatarSource((_, _) => Task.FromResult(AVATAR_URL));
        await using var avatars = new UserAvatarService(directories, images, source);
        var avatar = new UserAvatarView(avatars) { UserUid = 1, Width = 20, Height = 20 };
        var window = new Window { Content = avatar, Width = 200, Height = 100 };
        try
        {
            window.Show();
            await WaitForIdleAsync(avatar, window);
            await Task.Delay(50);
            await WaitForIdleAsync(avatar, window);

            Assert.Null(avatar.AvatarImage);
            Assert.Equal(2, downloads);
            Assert.Equal(2, source.Calls);
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task WaitForIdleAsync(UserAvatarView avatar, Window window)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var pending = avatar.PendingLoad;
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            if (ReferenceEquals(pending, avatar.PendingLoad) && pending != Task.CompletedTask)
            {
                return;
            }
        }
        Assert.Fail("Avatar recovery did not finish within its retry budget.");
    }
}
