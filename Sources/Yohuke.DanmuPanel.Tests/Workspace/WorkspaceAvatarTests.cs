using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Models.Workspace;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.ViewModels.Workspace;
using Yohuke.DanmuPanel.Views;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Workspace;

/// <summary>验证头像的界面展示、缓存恢复与异步生命周期。</summary>
public sealed class WorkspaceAvatarTests
{
    /// <summary>加载头像期间显示占位，加载后显示图片并保存主播身份和缓存位置。</summary>
    [AvaloniaFact]
    public async Task LoadedAvatarReplacesPlaceholderAndPersistsRoomIdentity()
    {
        using var directory = new MessageStoreTestDirectory();
        var imagePath = WriteImage(directory, "avatar.png");
        var room = new RoomTabState { RoomId = "123", Name = "123" };
        var workspace = new WorkspaceState { Rooms = [room] };
        await using var runtime = new PanelRuntime(new(directory.Path), new() { Workspaces = [workspace] });
        var completion = new TaskCompletionSource<RoomAvatarResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var avatars = new FakeRoomAvatarService { Resolve = (_, _, _, _) => completion.Task };
        using var view = new WorkspaceView(runtime, exception => throw exception, avatars);
        var window = new Window { Content = view, Width = 360, Height = 400 };
        try
        {
            window.Show();
            window.UpdateLayout();
            var node = Assert.Single(Assert.Single(view.Workspaces).Rooms);
            Assert.True(node.HasNoAvatar);
            completion.SetResult(new() { OwnerUid = "456", OwnerName = "主播", AvatarPath = imagePath });
            await node.LoadAvatarAsync(avatars);
            await WaitUntilAsync(() => runtime.ConfigurationStore.Load().Workspaces.FirstOrDefault()?.Rooms.FirstOrDefault()?.AvatarPath == imagePath);
            window.UpdateLayout();
            Assert.True(node.HasAvatar);
            Assert.False(node.HasNoAvatar);
            var image = Assert.Single(view.GetVisualDescendants().OfType<Image>());
            Assert.True(image.IsVisible);
            Assert.Same(node.AvatarImage, image.Source);
            var saved = runtime.ConfigurationStore.Load().Workspaces[0].Rooms[0];
            Assert.Equal("456", saved.OwnerUid);
            Assert.Equal("主播", saved.Name);
            Assert.Equal(imagePath, saved.AvatarPath);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>已有磁盘缓存立即可显示，刷新失败不清除旧头像。</summary>
    [AvaloniaFact]
    public async Task CachedAvatarRemainsVisibleWhenRefreshFails()
    {
        using var directory = new MessageStoreTestDirectory();
        var room = new RoomTabState { RoomId = "123", AvatarPath = WriteImage(directory, "cached.png") };
        using var node = new RoomTreeItemViewModel(new(), room);
        Assert.True(node.HasAvatar);
        var cached = node.AvatarImage;
        Assert.False(await node.LoadAvatarAsync(new FakeRoomAvatarService()));
        Assert.Same(cached, node.AvatarImage);
        Assert.True(node.HasAvatar);
    }

    /// <summary>修改主播 UID 后，旧异步结果不能覆盖新主播头像。</summary>
    [AvaloniaFact]
    public async Task PreviousOwnerResponseCannotReplaceNewOwnerAvatar()
    {
        using var directory = new MessageStoreTestDirectory();
        var oldImage = WriteImage(directory, "old.png");
        var currentImage = WriteImage(directory, "current.png");
        var room = new RoomTabState { RoomId = "123", OwnerUid = "1" };
        using var node = new RoomTreeItemViewModel(new(), room);
        var completion = new TaskCompletionSource<RoomAvatarResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeRoomAvatarService
        {
            Resolve = (_, owner, _, _) => owner == "1" ? completion.Task : Task.FromResult(new RoomAvatarResult
            {
                OwnerUid = "2", AvatarPath = currentImage
            })
        };
        var oldRequest = node.LoadAvatarAsync(service);
        room.OwnerUid = "2";
        Assert.True(await node.LoadAvatarAsync(service));
        completion.SetResult(new() { OwnerUid = "1", AvatarPath = oldImage });
        Assert.False(await oldRequest);
        Assert.Equal("2", room.OwnerUid);
        Assert.Equal(currentImage, room.AvatarPath);
        Assert.True(node.HasAvatar);
    }

    /// <summary>节点释放后忽略迟到结果并释放已加载的图片。</summary>
    [AvaloniaFact]
    public async Task DisposedRoomIgnoresLateAvatarResult()
    {
        using var directory = new MessageStoreTestDirectory();
        var room = new RoomTabState { RoomId = "123" };
        using var node = new RoomTreeItemViewModel(new(), room);
        var completion = new TaskCompletionSource<RoomAvatarResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = node.LoadAvatarAsync(new FakeRoomAvatarService { Resolve = (_, _, _, _) => completion.Task });
        node.Dispose();
        completion.SetResult(new() { OwnerUid = "456", AvatarPath = WriteImage(directory, "late.png") });
        Assert.False(await request);
        Assert.Equal("", room.OwnerUid);
        Assert.Equal("", room.AvatarPath);
        Assert.False(node.HasAvatar);
    }

    private static string WriteImage(MessageStoreTestDirectory directory, string name)
    {
        Directory.CreateDirectory(directory.Path);
        var path = Path.Combine(directory.Path, name);
        File.WriteAllBytes(path, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAEUlEQVR4nGMI6LnzH4QZYAwAXRQK3dFXl4QAAAAASUVORK5CYII="));
        return path;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition())
            {
                return;
            }
            await Task.Delay(10);
        }
        Assert.Fail("Avatar state was not persisted within the test timeout.");
    }
}
