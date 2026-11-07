using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Tests.Workspace;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views.Components;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Windowing;

/// <summary>验证共用用户头像的悬停大图、按需加载和资源回收。</summary>
public sealed class UserAvatarPreviewTests
{
    /// <summary>悬停从缓存原图独立解码预览，匹配屏幕缩放且不额外查询头像服务。</summary>
    [AvaloniaTheory]
    [InlineData(640, 640)]
    [InlineData(640, 320)]
    [InlineData(320, 640)]
    public async Task HoverLoadsLargerPreviewFromCachedFile(int width, int height)
    {
        using var directory = new MessageStoreTestDirectory();
        var path = WriteImage(directory, width, height);
        var service = new ControlledUserAvatarService { Resolve = (_, _) => Task.FromResult(path) };
        var avatar = CreateAvatar(service);
        var window = CreateWindow(avatar);
        Bitmap large;
        try
        {
            window.Show();
            await WaitUntilAsync(() => avatar.AvatarImage is not null, window);
            var small = avatar.AvatarImage!;
            var size = avatar.Bounds.Size;
            Assert.False(ToolTip.GetIsOpen(avatar));
            Assert.Null(ToolTip.GetTip(avatar));

            var preview = await OpenPreviewAsync(avatar, window);
            large = Assert.IsType<Bitmap>(preview.Source);
            Assert.NotSame(small, large);
            Assert.Equal((int)Math.Ceiling(160 * window.RenderScaling),
                Math.Max(large.PixelSize.Width, large.PixelSize.Height));
            Assert.InRange(Math.Abs(Math.Max(preview.Bounds.Width, preview.Bounds.Height) - 160), 0, 0.5);
            Assert.InRange(Math.Abs(preview.Bounds.Width / preview.Bounds.Height - (double)width / height), 0, 0.01);
            Assert.Equal(size, avatar.Bounds.Size);
            Assert.Single(service.RequestedUsers);

            window.MouseMove(new(280, 180));
            Pump(window);
            AssertClosed(avatar, preview, large);
            Assert.True(small.PixelSize.Width > 0);

            var reopened = await OpenPreviewAsync(avatar, window);
            var replacement = Assert.IsType<Bitmap>(reopened.Source);
            Assert.NotSame(large, replacement);
            large = replacement;
        }
        finally
        {
            window.Close();
        }
        AssertDisposed(large);
        Assert.Equal(0, service.Subscribers);
    }

    /// <summary>隐藏、用户换绑、回收和窗口关闭均立即关闭大图并释放位图。</summary>
    [AvaloniaTheory]
    [InlineData("hidden")]
    [InlineData("ancestor")]
    [InlineData("setting")]
    [InlineData("user")]
    [InlineData("detach")]
    [InlineData("window")]
    [InlineData("tooltip")]
    public async Task LifecycleChangesReleaseOpenPreview(string change)
    {
        using var directory = new MessageStoreTestDirectory();
        var path = WriteImage(directory, 640, 640);
        var service = new ControlledUserAvatarService { Resolve = (_, _) => Task.FromResult(path) };
        var avatar = CreateAvatar(service);
        var host = new Border { Child = avatar };
        var window = CreateWindow(host);
        try
        {
            window.Show();
            await WaitUntilAsync(() => avatar.AvatarImage is not null, window);
            var preview = await OpenPreviewAsync(avatar, window);
            var bitmap = Assert.IsType<Bitmap>(preview.Source);

            switch (change)
            {
                case "hidden":
                    avatar.IsVisible = false;
                    break;
                case "ancestor":
                    host.IsVisible = false;
                    break;
                case "setting":
                    avatar.ShowAvatar = false;
                    break;
                case "user":
                    avatar.UserUid = 2;
                    break;
                case "detach":
                    host.Child = null;
                    break;
                case "window":
                    window.Close();
                    break;
                case "tooltip":
                    ToolTip.SetIsOpen(avatar, false);
                    break;
            }

            AssertClosed(avatar, preview, bitmap);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>加载中的占位头像不显示空 tooltip，头像到达后无需移动鼠标即可预览。</summary>
    [AvaloniaFact]
    public async Task HoveredPlaceholderOpensOnlyAfterAvatarArrives()
    {
        using var directory = new MessageStoreTestDirectory();
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ControlledUserAvatarService { Resolve = (_, _) => completion.Task };
        var avatar = CreateAvatar(service);
        ToolTip.SetShowDelay(avatar, 0);
        var window = CreateWindow(avatar);
        try
        {
            window.Show();
            Pump(window);
            window.MouseMove(Center(avatar, window));
            Pump(window);
            Assert.False(ToolTip.GetIsOpen(avatar));
            Assert.Null(ToolTip.GetTip(avatar));

            completion.SetResult(WriteImage(directory, 640, 640));
            await WaitUntilAsync(() => ToolTip.GetIsOpen(avatar), window);
            Assert.Single(service.RequestedUsers);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>悬停等待期间离开头像会取消弹出，重新进入后仍可正常预览。</summary>
    [AvaloniaFact]
    public async Task LeavingBeforeDelayPreventsPreview()
    {
        using var directory = new MessageStoreTestDirectory();
        var path = WriteImage(directory, 640, 640);
        var avatar = CreateAvatar(new() { Resolve = (_, _) => Task.FromResult(path) });
        ToolTip.SetShowDelay(avatar, 40);
        var window = CreateWindow(avatar);
        try
        {
            window.Show();
            await WaitUntilAsync(() => avatar.AvatarImage is not null, window);
            window.MouseMove(Center(avatar, window));
            window.MouseMove(new(280, 180));
            await Task.Delay(80);
            Pump(window);
            Assert.False(ToolTip.GetIsOpen(avatar));
            Assert.Null(ToolTip.GetTip(avatar));

            await OpenPreviewAsync(avatar, window);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>预览解码尚未回到界面线程时离开或换绑，迟到的结果不会重新打开 tooltip。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LatePreviewCannotOpenAfterLeavingOrRebinding(bool rebind)
    {
        using var directory = new MessageStoreTestDirectory();
        var path = WriteImage(directory, 640, 640);
        var avatar = CreateAvatar(new() { Resolve = (_, _) => Task.FromResult(path) });
        var window = CreateWindow(avatar);
        try
        {
            window.Show();
            await WaitUntilAsync(() => avatar.AvatarImage is not null, window);
            ToolTip.SetShowDelay(avatar, 0);
            window.MouseMove(Center(avatar, window));
            var pending = avatar.PendingPreviewLoad;
            if (rebind)
            {
                avatar.UserUid = 0;
            }
            else
            {
                window.MouseMove(new(280, 180));
            }
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Pump(window);

            Assert.False(ToolTip.GetIsOpen(avatar));
            Assert.Null(ToolTip.GetTip(avatar));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>缓存文件不可读时不弹出空预览，也不会损坏已经显示的小头像。</summary>
    [AvaloniaFact]
    public async Task MissingPreviewFileKeepsThumbnailWithoutOpeningTooltip()
    {
        using var directory = new MessageStoreTestDirectory();
        var path = WriteImage(directory, 640, 640);
        var service = new ControlledUserAvatarService { Resolve = (_, _) => Task.FromResult(path) };
        var avatar = CreateAvatar(service);
        var window = CreateWindow(avatar);
        try
        {
            window.Show();
            await WaitUntilAsync(() => avatar.AvatarImage is not null, window);
            var small = avatar.AvatarImage;
            File.Delete(path);
            ToolTip.SetShowDelay(avatar, 0);
            window.MouseMove(Center(avatar, window));
            await avatar.PendingPreviewLoad.WaitAsync(TimeSpan.FromSeconds(5));
            Pump(window);

            Assert.False(ToolTip.GetIsOpen(avatar));
            Assert.Null(ToolTip.GetTip(avatar));
            Assert.Same(small, avatar.AvatarImage);
            Assert.True(small!.PixelSize.Width > 0);
            Assert.Single(service.RequestedUsers);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>滚动使头像离开视口时立即关闭预览并释放大图。</summary>
    [AvaloniaFact]
    public async Task ScrollingAvatarOutOfViewportReleasesPreview()
    {
        using var directory = new MessageStoreTestDirectory();
        var path = WriteImage(directory, 640, 640);
        var avatar = CreateAvatar(new() { Resolve = (_, _) => Task.FromResult(path) });
        var scroller = new ScrollViewer
        {
            Content = new StackPanel { Children = { avatar, new Border { Height = 600 } } }
        };
        var window = CreateWindow(scroller);
        try
        {
            window.Show();
            await WaitUntilAsync(() => avatar.AvatarImage is not null, window);
            var preview = await OpenPreviewAsync(avatar, window);
            var bitmap = Assert.IsType<Bitmap>(preview.Source);

            scroller.Offset = new(0, 200);
            Pump(window);

            AssertClosed(avatar, preview, bitmap);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>头像支持悬停后仍把鼠标点击冒泡给消息行，不影响四类消息的激活。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.Danmu)]
    [InlineData(MessageKind.SuperChat)]
    [InlineData(MessageKind.Gift)]
    [InlineData(MessageKind.Guard)]
    public async Task ClickingHoveredAvatarStillActivatesMessageRow(MessageKind kind)
    {
        using var directory = new MessageStoreTestDirectory();
        var path = WriteImage(directory, 640, 640);
        var service = new ControlledUserAvatarService { Resolve = (_, _) => Task.FromResult(path) };
        var row = new MessageFeedRow(kind, service)
        {
            DataContext = new MessageItemViewModel(new MessageRecord
            {
                Kind = kind, UserUid = 1, UserName = "头像测试", Content = "测试消息"
            }, new PanelSettings { ShowUserPinyin = false, ShowDanmuPinyin = false }, new PinyinService())
        };
        var clicks = 0;
        row.Click += (_, _) => clicks++;
        var window = CreateWindow(row);
        try
        {
            window.Show();
            Pump(window);
            var avatar = Assert.Single(row.GetVisualDescendants().OfType<UserAvatarView>());
            await WaitUntilAsync(() => avatar.AvatarImage is not null, window);
            var preview = await OpenPreviewAsync(avatar, window);
            var bitmap = Assert.IsType<Bitmap>(preview.Source);
            var point = Center(avatar, window);

            window.MouseDown(point, MouseButton.Left);
            AssertClosed(avatar, preview, bitmap);
            window.MouseUp(point, MouseButton.Left);

            Assert.Equal(1, clicks);
            AssertClosed(avatar, preview, bitmap);
            Pump(window);
            window.MouseMove(point + new Vector(1, 0));
            await WaitUntilAsync(() => ToolTip.GetIsOpen(avatar), window);
        }
        finally
        {
            window.Close();
        }
    }

    private static UserAvatarView CreateAvatar(ControlledUserAvatarService service)
    {
        return new(service)
        {
            UserUid = 1,
            Width = 32,
            Height = 32,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
    }

    private static Window CreateWindow(Control content)
    {
        return new() { Content = new Border { Padding = new(12), Child = content }, Width = 300, Height = 200 };
    }

    private static async Task<Image> OpenPreviewAsync(UserAvatarView avatar, Window window)
    {
        ToolTip.SetShowDelay(avatar, 0);
        window.MouseMove(Center(avatar, window));
        await WaitUntilAsync(() => ToolTip.GetIsOpen(avatar), window);
        return Assert.IsType<Image>(Assert.IsType<ToolTip>(ToolTip.GetTip(avatar)).Content);
    }

    private static Point Center(Control control, Window window)
    {
        return control.TranslatePoint(new(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
    }

    private static string WriteImage(MessageStoreTestDirectory directory, int width, int height)
    {
        Directory.CreateDirectory(directory.Path);
        var path = Path.Combine(directory.Path, "avatar.png");
        using var image = new WriteableBitmap(new(width, height), new(96, 96), Avalonia.Platform.PixelFormat.Bgra8888);
        image.Save(path);
        return path;
    }

    private static void AssertClosed(UserAvatarView avatar, Image preview, Bitmap bitmap)
    {
        Assert.False(ToolTip.GetIsOpen(avatar));
        Assert.Null(ToolTip.GetTip(avatar));
        Assert.Null(preview.Source);
        AssertDisposed(bitmap);
    }

    private static void AssertDisposed(Bitmap bitmap)
    {
        Assert.Throws<NullReferenceException>(() => bitmap.PixelSize);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, Window window)
    {
        for (var attempt = 0; attempt < 150; attempt++)
        {
            Pump(window);
            if (condition())
            {
                return;
            }
            await Task.Delay(10);
        }
        Assert.Fail("Avatar preview did not reach the expected state.");
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
