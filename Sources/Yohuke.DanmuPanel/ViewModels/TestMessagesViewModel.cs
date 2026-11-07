using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Models.Testing;
using Yohuke.DanmuPanel.Services;

namespace Yohuke.DanmuPanel.ViewModels;

/// <summary>管理本地测试消息表单、目标房间和异步事务。</summary>
public sealed partial class TestMessagesViewModel : ObservableObject, IDisposable
{
    private readonly TestMessageService service;
    private readonly Action<Exception> reportError;
    private RoomTabState? room;
    private bool busy;
    private bool stopping;
    private string? statusKey;
    private int statusCount;
    private string statusRoom = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsMedal), nameof(ShowsText), nameof(ShowsGift), nameof(ShowsPrice), nameof(ShowsQuantity), nameof(ShowsGuard))]
    private int kindIndex;
    [ObservableProperty] private string userName = Localization.Get("TestMessages.SampleUser");
    [ObservableProperty] private string medalName = "";
    [ObservableProperty] private decimal? medalLevel = 0;
    [ObservableProperty] private int medalGuardIndex;
    [ObservableProperty] private string messageContent = Localization.Get("TestMessages.SampleContent");
    [ObservableProperty] private string giftName = Localization.Get("TestMessages.SampleGift");
    [ObservableProperty] private string translation = "";
    [ObservableProperty] private decimal? price = 30;
    [ObservableProperty] private decimal? quantity = 1;
    [ObservableProperty] private decimal? batchCount = 100;
    [ObservableProperty] private int guardIndex = 2;

    public IReadOnlyList<TestMessageChoice> KindChoices { get; } =
        [new("Workbench.Panel.Danmu"), new("Workbench.Panel.SuperChat"), new("Workbench.Panel.Gift"), new("Workbench.Panel.Guard")];
    public IReadOnlyList<TestMessageChoice> GuardChoices { get; } = [new("Panel.Guard.1"), new("Panel.Guard.2"), new("Panel.Guard.3")];
    public IReadOnlyList<TestMessageChoice> MedalGuardChoices { get; } = [new("TestMessages.NoGuard"), new("Panel.Guard.3"), new("Panel.Guard.2"), new("Panel.Guard.1")];
    public decimal MaximumBatchCount => TestMessageRequest.MAX_BATCH_COUNT;
    public bool ShowsMedal => KindIndex == (int)MessageKind.Danmu;
    public bool ShowsText => KindIndex is (int)MessageKind.Danmu or (int)MessageKind.SuperChat;
    public bool ShowsGift => KindIndex == (int)MessageKind.Gift;
    public bool ShowsPrice => KindIndex != (int)MessageKind.Danmu;
    public bool ShowsQuantity => KindIndex is (int)MessageKind.Gift or (int)MessageKind.Guard;
    public bool ShowsGuard => KindIndex == (int)MessageKind.Guard;
    public string TargetText => room is null ? Localization.Get("TestMessages.NoRoom") : Localization.Format("TestMessages.Target", room.Name, room.RoomId);
    public bool HasStatus => statusKey is not null;
    public string StatusText => statusKey is null ? "" : Localization.Format(statusKey, statusCount, statusRoom);
    public IAsyncRelayCommand AddSingleCommand { get; }
    public IAsyncRelayCommand AddBatchCommand { get; }
    public IAsyncRelayCommand ClearCommand { get; }

    /// <summary>连接现有本地测试消息服务和错误报告。</summary>
    public TestMessagesViewModel(PanelRuntime runtime, Action<Exception> reportError)
    {
        service = runtime.TestMessages;
        this.reportError = reportError;
        AddSingleCommand = new AsyncRelayCommand(() => AddAsync(1), CanExecute);
        AddBatchCommand = new AsyncRelayCommand(() => AddAsync(decimal.ToInt32(BatchCount ?? 1)), CanExecute);
        ClearCommand = new AsyncRelayCommand(ClearAsync, CanExecute);
        Localization.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>切换本地测试消息的目标房间；未选择房间时禁用操作。</summary>
    public void SelectRoom(RoomTabState? selected)
    {
        if (room is not null)
        {
            room.PropertyChanged -= OnRoomChanged;
        }
        room = selected;
        if (room is not null)
        {
            room.PropertyChanged += OnRoomChanged;
        }
        statusKey = null;
        RefreshStatus();
        RefreshTarget();
        RefreshCommands();
    }

    /// <summary>退出前停止新操作并等待已发起的本地事务完成。</summary>
    public Task FinishPendingAsync()
    {
        stopping = true;
        RefreshCommands();
        return Task.WhenAll(AddSingleCommand.ExecutionTask ?? Task.CompletedTask,
            AddBatchCommand.ExecutionTask ?? Task.CompletedTask, ClearCommand.ExecutionTask ?? Task.CompletedTask);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        stopping = true;
        Localization.LanguageChanged -= OnLanguageChanged;
        if (room is not null)
        {
            room.PropertyChanged -= OnRoomChanged;
        }
        RefreshCommands();
    }

    private async Task AddAsync(int count)
    {
        var selectedRoom = room!;
        var selectedKind = (MessageKind)KindIndex;
        if (string.IsNullOrWhiteSpace(UserName) ||
            selectedKind is MessageKind.Danmu or MessageKind.SuperChat && string.IsNullOrWhiteSpace(MessageContent) ||
            selectedKind == MessageKind.Gift && string.IsNullOrWhiteSpace(GiftName))
        {
            SetStatus("TestMessages.Required", 0, "");
            return;
        }
        var request = new TestMessageRequest
        {
            Kind = selectedKind, UserName = UserName!, Content = MessageContent ?? "",
            MedalName = MedalName ?? "", MedalLevel = decimal.ToInt32(MedalLevel ?? 0),
            MedalGuardLevel = MedalGuardIndex switch
            {
                0 => 0,
                1 => 3,
                2 => 2,
                3 => 1,
                _ => throw new InvalidOperationException()
            },
            GiftName = GiftName ?? "", UnitPrice = Price ?? 0,
            Quantity = decimal.ToInt32(Quantity ?? 1), GuardLevel = GuardIndex + 1,
            Translation = Translation ?? "", Count = count
        };
        await RunAsync(async () =>
        {
            var added = await service.AddAsync(selectedRoom, request);
            SetStatus("TestMessages.Added", added, selectedRoom.Name);
        });
    }

    private async Task ClearAsync()
    {
        var selectedRoom = room!;
        await RunAsync(async () =>
        {
            var removed = await service.ClearAsync(selectedRoom);
            SetStatus("TestMessages.Cleared", removed, selectedRoom.Name);
        });
    }

    private async Task RunAsync(Func<Task> action)
    {
        busy = true;
        RefreshCommands();
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            SetStatus("TestMessages.Failed", 0, "");
            reportError(exception);
        }
        finally
        {
            busy = false;
            RefreshCommands();
        }
    }

    private bool CanExecute()
    {
        return room is not null && !busy && !stopping;
    }

    private void RefreshCommands()
    {
        AddSingleCommand.NotifyCanExecuteChanged();
        AddBatchCommand.NotifyCanExecuteChanged();
        ClearCommand.NotifyCanExecuteChanged();
    }

    private void RefreshTarget()
    {
        OnPropertyChanged(nameof(TargetText));
    }

    private void SetStatus(string key, int count, string roomName)
    {
        statusKey = key;
        statusCount = count;
        statusRoom = roomName;
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        OnPropertyChanged(nameof(HasStatus));
        OnPropertyChanged(nameof(StatusText));
    }

    private void OnRoomChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RoomTabState.Name))
        {
            RefreshTarget();
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RefreshTarget();
        RefreshStatus();
        foreach (var choice in KindChoices.Concat(GuardChoices).Concat(MedalGuardChoices))
        {
            choice.RefreshLocalization();
        }
    }

}
