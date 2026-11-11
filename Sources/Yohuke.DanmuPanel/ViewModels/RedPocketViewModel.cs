using CommunityToolkit.Mvvm.ComponentModel;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.ViewModels;

/// <summary>同一红包的内容与开奖结果，独立保留中奖名单展开状态。</summary>
public sealed class RedPocketViewModel : ObservableObject
{
    private bool isExpanded;
    private bool showUserAvatars = true;

    public RedPocketData Data { get; private set; } = new();
    public string Status { get; private set; } = "";
    public string DrawTime { get; private set; } = "";
    public string Value { get; private set; } = "";
    public string ResultsTitle { get; private set; } = "";
    public bool ShowUserAvatars
    {
        get => showUserAvatars;
        internal set => SetProperty(ref showUserAvatars, value);
    }
    public IReadOnlyList<RedPocketAwardViewModel> Awards { get; private set; } = [];
    public IReadOnlyList<RedPocketWinnerViewModel> Winners { get; private set; } = [];
    public bool HasDrawTime => DrawTime.Length > 0;
    public bool HasValue => Value.Length > 0;
    public bool HasDanmu => Data.Danmu.Length > 0;
    public bool HasAwards => Data.Awards.Count > 0;
    public bool HasWinners => Winners.Count > 0;
    public bool IsContentPending => !Data.HasDetails && !HasAwards && !HasValue;

    public bool IsExpanded
    {
        get => isExpanded;
        set => SetProperty(ref isExpanded, value);
    }

    internal void Update(RedPocketData? data)
    {
        Data = data ?? new();
        if (!Localization.IsInitialized)
        {
            return;
        }
        Status = Localization.Get(Data.HasResult ? "RedPocket.Drawn" : "RedPocket.Waiting");
        DrawTime = Data.EndTime > 0
            ? Localization.Format("RedPocket.DrawTime", DateTimeOffset.FromUnixTimeSeconds(Data.EndTime)
                .ToLocalTime().ToString("MM-dd HH:mm:ss"))
            : "";
        Value = FormatValue(Data);
        Awards = Data.Awards.Select(award => new RedPocketAwardViewModel(award)).ToArray();
        Winners = Data.Winners.Select(winner => new RedPocketWinnerViewModel(winner)).ToArray();
        ResultsTitle = Localization.Format("RedPocket.Winners", Winners.Count);
        OnPropertyChanged(nameof(Data));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(DrawTime));
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(ResultsTitle));
        OnPropertyChanged(nameof(Awards));
        OnPropertyChanged(nameof(Winners));
        OnPropertyChanged(nameof(HasDrawTime));
        OnPropertyChanged(nameof(HasValue));
        OnPropertyChanged(nameof(HasDanmu));
        OnPropertyChanged(nameof(HasAwards));
        OnPropertyChanged(nameof(HasWinners));
        OnPropertyChanged(nameof(IsContentPending));
    }

    internal static string FormatValue(RedPocketData data, bool includeGiftLabel = true)
    {
        if (data.Type is 3 or 5 && (data.TotalBattery ?? data.TotalPrice) is { } battery)
        {
            return Localization.Format("RedPocket.BatteryValue", battery / 100m);
        }
        if (data.Type == 4)
        {
            return Localization.Get("RedPocket.Intimacy");
        }
        if (data.TotalPrice is { } price)
        {
            return Localization.Format(includeGiftLabel ? "RedPocket.GiftValue" : "Panel.EventSummary.Value", price / 1000m);
        }
        return data.Price is { } senderPrice
            ? Localization.Format("RedPocket.BatteryValue", senderPrice)
            : "";
    }
}
