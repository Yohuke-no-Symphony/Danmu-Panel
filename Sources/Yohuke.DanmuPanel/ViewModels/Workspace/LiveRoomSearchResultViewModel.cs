using CommunityToolkit.Mvvm.ComponentModel;
using Yohuke.DanmuCore.Models.Search;
using Yohuke.DanmuPanel.I18n;

namespace Yohuke.DanmuPanel.ViewModels.Workspace;

/// <summary>为直播间搜索结果提供可绑定的来源和本地化说明。</summary>
public sealed class LiveRoomSearchResultViewModel : ObservableObject
{
    private readonly LiveRoomSearchResult result;
    private LiveRoomSearchSource sources;

    /// <summary>使用平台搜索结果创建展示对象。</summary>
    public LiveRoomSearchResultViewModel(LiveRoomSearchResult result)
    {
        this.result = result;
        sources = result.Sources;
    }

    /// <summary>直播间 ID。</summary>
    public string RoomId => result.RoomId;

    /// <summary>直播间短房间号。</summary>
    public string ShortRoomId => result.ShortRoomId;

    /// <summary>主播 UID。</summary>
    public string OwnerUid => result.OwnerUid;

    /// <summary>主播昵称。</summary>
    public string OwnerName => result.OwnerName;

    /// <summary>直播标题。</summary>
    public string Title => result.Title;

    /// <summary>主播是否正在直播。</summary>
    public bool IsLive => result.IsLive;

    /// <summary>合并到该直播间的搜索来源。</summary>
    public LiveRoomSearchSource Sources
    {
        get => sources;
        set
        {
            if (SetProperty(ref sources, value))
            {
                OnPropertyChanged(nameof(SourceLabel));
            }
        }
    }

    /// <summary>本地化的搜索来源说明。</summary>
    public string SourceLabel => GetSourceLabel(Sources);

    /// <summary>本地化的房间与直播状态说明。</summary>
    public string DetailLine => Localization.Format("Room.Search.Detail", RoomId, OwnerUid,
        Localization.Get(IsLive ? "Room.Search.Live" : "Room.Search.Offline"));

    internal static string GetSourceLabel(LiveRoomSearchSource source)
    {
        return string.Join(" · ", Enum.GetValues<LiveRoomSearchSource>()
            .Where(value => value != LiveRoomSearchSource.None && source.HasFlag(value))
            .Select(value => Localization.Get($"Room.Search.Source.{value}")));
    }

    internal void RefreshLocalization()
    {
        OnPropertyChanged(nameof(SourceLabel));
        OnPropertyChanged(nameof(DetailLine));
    }
}
