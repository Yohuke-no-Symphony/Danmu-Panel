using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Media.Imaging;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.Logging;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;

namespace Yohuke.DanmuPanel.ViewModels.Workspace;

/// <summary>直播间树节点的选择和连接状态。</summary>
public sealed partial class RoomTreeItemViewModel : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private Bitmap? avatarImage;
    private string avatarRoomId;
    private string avatarOwnerUid;
    private string? requestedRoomId;
    private string? requestedOwnerUid;
    private Task<bool>? avatarLoad;
    private bool isDisposed;
    public WorkspaceState Workspace { get; }
    public RoomTabState State { get; }
    public Bitmap? AvatarImage => avatarImage;
    public bool HasAvatar => avatarImage is not null;
    public bool HasNoAvatar => !HasAvatar;
    [ObservableProperty] private bool isSelected;
    [ObservableProperty] private bool isListening;
    [ObservableProperty] private string connectionStatus = "";
    [ObservableProperty] private string avatarPlaceholderText = "";

    /// <summary>创建直播间与所属工作区的显式关联。</summary>
    public RoomTreeItemViewModel(WorkspaceState workspace, RoomTabState state)
    {
        Workspace = workspace;
        State = state;
        avatarRoomId = state.RoomId;
        avatarOwnerUid = state.OwnerUid;
        avatarImage = ReadAvatar(state.AvatarPath);
    }

    /// <summary>异步补齐主播身份和头像，房间身份变化后丢弃迟到的结果。</summary>
    public Task<bool> LoadAvatarAsync(IRoomAvatarService service, string? cookie = null)
    {
        if (isDisposed)
        {
            return Task.FromResult(false);
        }
        var roomId = State.RoomId;
        var ownerUid = State.OwnerUid;
        if (avatarRoomId != roomId || avatarOwnerUid != ownerUid)
        {
            State.AvatarPath = "";
            ReplaceAvatar(null);
            avatarRoomId = roomId;
            avatarOwnerUid = ownerUid;
        }
        if (requestedRoomId == roomId && requestedOwnerUid == ownerUid && avatarLoad is not null)
        {
            return avatarLoad.IsCompleted ? Task.FromResult(false) : avatarLoad;
        }
        requestedRoomId = roomId;
        requestedOwnerUid = ownerUid;
        avatarLoad = FetchAvatarAsync(service, roomId, ownerUid, cookie);
        return avatarLoad;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }
        isDisposed = true;
        lifetime.Cancel();
        lifetime.Dispose();
        ReplaceAvatar(null);
    }

    private async Task<bool> FetchAvatarAsync(IRoomAvatarService service, string roomId, string ownerUid, string? cookie)
    {
        try
        {
            var result = await service.GetRoomAvatarResultAsync(roomId, ownerUid, cookie, lifetime.Token);
            if (isDisposed || State.RoomId != roomId || State.OwnerUid != ownerUid || string.IsNullOrWhiteSpace(result.AvatarPath))
            {
                return false;
            }
            var image = ReadAvatar(result.AvatarPath);
            if (image is null)
            {
                return false;
            }
            if (!string.IsNullOrWhiteSpace(result.OwnerUid))
            {
                State.OwnerUid = result.OwnerUid;
                avatarOwnerUid = result.OwnerUid;
                requestedOwnerUid = result.OwnerUid;
            }
            if ((string.IsNullOrWhiteSpace(State.Name) || State.Name == roomId) && !string.IsNullOrWhiteSpace(result.OwnerName))
            {
                State.Name = result.OwnerName;
            }
            State.AvatarPath = result.AvatarPath;
            ReplaceAvatar(image);
            return true;
        }
        catch (OperationCanceledException) when (isDisposed || State.RoomId != roomId || State.OwnerUid != ownerUid)
        {
            return false;
        }
    }

    private void ReplaceAvatar(Bitmap? image)
    {
        var previous = avatarImage;
        avatarImage = image;
        OnPropertyChanged(nameof(AvatarImage));
        OnPropertyChanged(nameof(HasAvatar));
        OnPropertyChanged(nameof(HasNoAvatar));
        previous?.Dispose();
    }

    private static Bitmap? ReadAvatar(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }
        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, 64);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           ArgumentException or NotSupportedException or InvalidOperationException)
        {
            StartupLog.Append(Localization.Get("Room.Log.AvatarLoadFailed"), AppLogLevel.WARNING,
                Localization.Get("Services.Avatar.Source"), exception);
            return null;
        }
    }
}
