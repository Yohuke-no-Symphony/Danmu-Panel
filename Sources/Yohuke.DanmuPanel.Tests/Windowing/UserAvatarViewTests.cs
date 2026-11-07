using Avalonia.Controls;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Tests.Workspace;
using Yohuke.DanmuPanel.Views.Components;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Windowing;

/// <summary>验证圆形头像的异步换绑、开关与可见生命周期。</summary>
public sealed class UserAvatarViewTests
{
    /// <summary>有效图片替换圆形占位，加载前后占用同一尺寸并限制解码大小。</summary>
    [AvaloniaFact]
    public async Task AvatarLoadsIntoCircleWithoutChangingLayout()
    {
        using var directory = new MessageStoreTestDirectory();
        var path = WriteImage(directory, "avatar.png");
        var completion = NewCompletion();
        var service = new ControlledUserAvatarService { Resolve = (_, _) => completion.Task };
        var avatar = new UserAvatarView(service) { UserUid = 1, Width = 20, Height = 20 };
        var window = new Window { Content = avatar, Width = 200, Height = 100 };
        try
        {
            window.Show();
            Pump(window);
            Assert.Null(avatar.AvatarImage);
            var size = avatar.Bounds.Size;
            completion.SetResult(path);
            await WaitUntilAsync(() => avatar.AvatarImage is not null, window);

            Assert.Equal(path, avatar.CurrentAvatarPath);
            Assert.Equal(size, avatar.Bounds.Size);
            Assert.InRange(avatar.AvatarImage!.PixelSize.Width, 1, 96);
            var circle = Assert.Single(avatar.GetVisualDescendants().OfType<Ellipse>());
            Assert.Same(avatar.AvatarImage, Assert.IsType<ImageBrush>(circle.Fill).Source);
        }
        finally
        {
            window.Close();
        }
        Assert.Null(avatar.AvatarImage);
        Assert.Equal(0, service.Subscribers);
    }

    /// <summary>同一组件快速换绑时旧请求即使忽略取消，也不能覆盖新用户。</summary>
    [AvaloniaFact]
    public async Task LatePreviousUserCannotReplaceCurrentAvatar()
    {
        using var directory = new MessageStoreTestDirectory();
        var first = NewCompletion();
        var second = NewCompletion();
        var service = new ControlledUserAvatarService { Resolve = (uid, _) => uid == 1 ? first.Task : second.Task };
        var avatar = new UserAvatarView(service) { UserUid = 1, Width = 20, Height = 20 };
        var window = new Window { Content = avatar, Width = 200, Height = 100 };
        try
        {
            window.Show();
            Pump(window);
            var oldRequest = avatar.PendingLoad;
            avatar.UserUid = 2;
            second.SetResult(WriteImage(directory, "second.png"));
            await WaitUntilAsync(() => avatar.CurrentAvatarPath.EndsWith("second.png"), window);
            var current = avatar.AvatarImage;
            first.SetResult(WriteImage(directory, "first.png"));
            await oldRequest;
            Pump(window);

            Assert.Equal(new long[] { 1, 2 }, service.RequestedUsers);
            Assert.Same(current, avatar.AvatarImage);
            avatar.UserUid = 0;
            Assert.Null(avatar.AvatarImage);
            Assert.Equal("", avatar.CurrentAvatarPath);
            Assert.Equal(2, service.RequestedUsers.Count);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>关闭头像不查询服务，开启后加载，关闭时迟到结果被丢弃。</summary>
    [AvaloniaFact]
    public async Task DisabledAvatarDoesNotLoadAndIgnoresPendingResponse()
    {
        using var directory = new MessageStoreTestDirectory();
        var completion = NewCompletion();
        var service = new ControlledUserAvatarService { Resolve = (_, _) => completion.Task };
        var avatar = new UserAvatarView(service) { UserUid = 1, ShowAvatar = false, Width = 20, Height = 20 };
        var window = new Window { Content = avatar, Width = 200, Height = 100 };
        try
        {
            window.Show();
            Pump(window);
            Assert.Empty(service.RequestedUsers);
            Assert.False(avatar.IsVisible);
            avatar.ShowAvatar = true;
            Pump(window);
            Assert.Single(service.RequestedUsers);
            var request = avatar.PendingLoad;
            avatar.ShowAvatar = false;
            completion.SetResult(WriteImage(directory, "late.png"));
            await request;
            Pump(window);

            Assert.Null(avatar.AvatarImage);
            Assert.False(avatar.IsVisible);
            service.NotifyChanged(1);
            Pump(window);
            Assert.Single(service.RequestedUsers);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>更新通知只刷新当前用户，新头像准备好以前保留旧头像。</summary>
    [AvaloniaFact]
    public async Task UpdateNotificationKeepsOldImageUntilReplacementIsReady()
    {
        using var directory = new MessageStoreTestDirectory();
        var firstPath = WriteImage(directory, "first.png");
        var secondPath = WriteImage(directory, "second.png");
        var completion = NewCompletion();
        var service = new ControlledUserAvatarService { Resolve = (_, _) => Task.FromResult(firstPath) };
        var avatar = new UserAvatarView(service) { UserUid = 1, Width = 20, Height = 20 };
        var window = new Window { Content = avatar, Width = 200, Height = 100 };
        try
        {
            window.Show();
            await WaitUntilAsync(() => avatar.AvatarImage is not null, window);
            var previous = avatar.AvatarImage;
            service.Resolve = (_, _) => completion.Task;
            service.NotifyChanged(2);
            Pump(window);
            Assert.Single(service.RequestedUsers);
            service.NotifyChanged(1);
            Pump(window);
            Assert.Same(previous, avatar.AvatarImage);
            completion.SetResult(secondPath);
            await WaitUntilAsync(() => avatar.CurrentAvatarPath == secondPath, window);

            Assert.NotSame(previous, avatar.AvatarImage);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>移除组件释放图片和订阅，重新挂载恢复缓存头像。</summary>
    [AvaloniaFact]
    public async Task DetachingClearsResourcesAndReattachingReloads()
    {
        using var directory = new MessageStoreTestDirectory();
        var path = WriteImage(directory, "avatar.png");
        var service = new ControlledUserAvatarService { Resolve = (_, _) => Task.FromResult(path) };
        var avatar = new UserAvatarView(service) { UserUid = 1, Width = 20, Height = 20 };
        var host = new Border { Child = avatar };
        var window = new Window { Content = host, Width = 200, Height = 100 };
        try
        {
            window.Show();
            await WaitUntilAsync(() => avatar.AvatarImage is not null, window);
            host.Child = null;
            Assert.Null(avatar.AvatarImage);
            Assert.Equal(0, service.Subscribers);
            host.Child = avatar;
            await WaitUntilAsync(() => avatar.AvatarImage is not null, window);

            Assert.Equal(path, avatar.CurrentAvatarPath);
            Assert.Equal(1, service.Subscribers);
            Assert.Equal(2, service.RequestedUsers.Count);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>无效图片不会让消息行抛出未观察异常。</summary>
    [AvaloniaFact]
    public async Task InvalidImageKeepsPlaceholder()
    {
        using var directory = new MessageStoreTestDirectory();
        Directory.CreateDirectory(directory.Path);
        var path = Path.Combine(directory.Path, "broken.png");
        File.WriteAllBytes(path, new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var service = new ControlledUserAvatarService { Resolve = (_, _) => Task.FromResult(path) };
        var avatar = new UserAvatarView(service) { UserUid = 1, Width = 20, Height = 20 };
        var window = new Window { Content = avatar, Width = 200, Height = 100 };
        try
        {
            window.Show();
            Pump(window);
            await avatar.PendingLoad;
            Assert.Null(avatar.AvatarImage);
            Assert.InRange(service.RequestedUsers.Count, 1, 2);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>坏图反馈给共享服务后，只立即重试一次并恢复可读头像。</summary>
    [AvaloniaFact]
    public async Task DecodeFailureInvalidatesCacheAndRetriesOnce()
    {
        using var directory = new MessageStoreTestDirectory();
        Directory.CreateDirectory(directory.Path);
        var brokenPath = Path.Combine(directory.Path, "broken.png");
        File.WriteAllBytes(brokenPath, new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var repairedPath = WriteImage(directory, "repaired.png");
        var currentPath = brokenPath;
        var service = new ControlledUserAvatarService { Resolve = (_, _) => Task.FromResult(currentPath) };
        service.OnInvalidate = (_, _) => currentPath = repairedPath;
        var avatar = new UserAvatarView(service) { UserUid = 1, Width = 20, Height = 20 };
        var window = new Window { Content = avatar, Width = 200, Height = 100 };
        try
        {
            window.Show();
            await WaitUntilAsync(() => avatar.AvatarImage is not null, window);
            Assert.Equal(repairedPath, avatar.CurrentAvatarPath);
            Assert.Equal((1L, brokenPath), Assert.Single(service.Invalidations));
            Assert.Equal(2, service.RequestedUsers.Count);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>运行环境先释放头像服务时，可见行不会留下未观察异常。</summary>
    [AvaloniaFact]
    public async Task ServiceDisposalDuringVisibleLoadIsHandled()
    {
        var completion = NewCompletion();
        var service = new ControlledUserAvatarService { Resolve = (_, _) => completion.Task };
        var avatar = new UserAvatarView(service) { UserUid = 1, Width = 20, Height = 20 };
        var window = new Window { Content = avatar, Width = 200, Height = 100 };
        try
        {
            window.Show();
            Pump(window);
            completion.SetException(new ObjectDisposedException("avatar service"));
            await avatar.PendingLoad;
            Assert.Null(avatar.AvatarImage);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>绑定更换头像服务时解绑旧订阅，并拒绝旧服务忽略取消后返回的图片。</summary>
    [AvaloniaFact]
    public async Task ReplacingBoundServiceCancelsPreviousLoadAndTransfersSubscriptions()
    {
        using var directory = new MessageStoreTestDirectory();
        var previousCompletion = NewCompletion();
        var previous = new ControlledUserAvatarService { Resolve = (_, _) => previousCompletion.Task };
        var currentPath = WriteImage(directory, "current-service.png");
        var current = new ControlledUserAvatarService { Resolve = (_, _) => Task.FromResult(currentPath) };
        var avatar = new UserAvatarView { AvatarService = previous, UserUid = 1, Width = 20, Height = 20 };
        var window = new Window { Content = avatar, Width = 200, Height = 100 };
        try
        {
            window.Show();
            Pump(window);
            var pending = avatar.PendingLoad;
            avatar.AvatarService = current;
            await WaitUntilAsync(() => avatar.CurrentAvatarPath == currentPath, window);
            previousCompletion.SetResult(WriteImage(directory, "previous-service.png"));
            await pending;
            Pump(window);

            Assert.Equal(0, previous.Subscribers);
            Assert.Equal(1, current.Subscribers);
            Assert.Equal(currentPath, avatar.CurrentAvatarPath);
            previous.NotifyChanged(1);
            Pump(window);
            Assert.Single(current.RequestedUsers);
        }
        finally
        {
            window.Close();
        }
        Assert.Equal(0, current.Subscribers);
    }

    /// <summary>损坏图片失效请求迟到时，已清除或替换的服务不能触发旧重试。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangingServiceDuringInvalidationCannotRetryPreviousRequest(bool replace)
    {
        using var directory = new MessageStoreTestDirectory();
        Directory.CreateDirectory(directory.Path);
        var brokenPath = Path.Combine(directory.Path, "broken-avatar.png");
        await File.WriteAllTextAsync(brokenPath, "invalid image");
        var invalidationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invalidationCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previous = new ControlledUserAvatarService
        {
            Resolve = (_, _) => Task.FromResult(brokenPath),
            OnInvalidateAsync = (_, _, _) =>
            {
                invalidationStarted.TrySetResult();
                return invalidationCompleted.Task;
            }
        };
        var currentPath = WriteImage(directory, "replacement-avatar.png");
        var current = new ControlledUserAvatarService { Resolve = (_, _) => Task.FromResult(currentPath) };
        var avatar = new UserAvatarView(previous) { UserUid = 1, Width = 20, Height = 20 };
        var window = new Window { Content = avatar, Width = 200, Height = 100 };
        try
        {
            window.Show();
            await WaitUntilAsync(() => invalidationStarted.Task.IsCompleted, window);
            var previousRequest = avatar.PendingLoad;
            avatar.AvatarService = replace ? current : null;
            invalidationCompleted.SetResult();
            await previousRequest;
            if (replace)
            {
                await WaitUntilAsync(() => avatar.CurrentAvatarPath == currentPath, window);
                Assert.Single(current.RequestedUsers);
            }
            else
            {
                Assert.Null(avatar.AvatarImage);
            }
            Assert.Single(previous.RequestedUsers);
            Assert.Equal(0, previous.Subscribers);
        }
        finally
        {
            invalidationCompleted.TrySetResult();
            window.Close();
        }
    }

    private static TaskCompletionSource<string> NewCompletion()
    {
        return new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static string WriteImage(MessageStoreTestDirectory directory, string name)
    {
        Directory.CreateDirectory(directory.Path);
        var path = Path.Combine(directory.Path, name);
        using var bitmap = new WriteableBitmap(new(200, 200), new(96, 96), Avalonia.Platform.PixelFormat.Bgra8888);
        bitmap.Save(path);
        return path;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, Window window)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            Pump(window);
            if (condition())
            {
                return;
            }
            await Task.Delay(10);
        }
        Assert.Fail("Avatar UI did not reach the expected state.");
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
